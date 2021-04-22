#region License
// Copyright (c) 2021 Jake Fowler
//
// Permission is hereby granted, free of charge, to any person 
// obtaining a copy of this software and associated documentation 
// files (the "Software"), to deal in the Software without 
// restriction, including without limitation the rights to use, 
// copy, modify, merge, publish, distribute, sublicense, and/or sell 
// copies of the Software, and to permit persons to whom the 
// Software is furnished to do so, subject to the following 
// conditions:
//
// The above copyright notice and this permission notice shall be 
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, 
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES 
// OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND 
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT 
// HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, 
// WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING 
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR 
// OTHER DEALINGS IN THE SOFTWARE.
#endregion

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ExcelDna.Integration;

namespace Cmdty.Storage.Excel
{
    public class MultiFactorCalcWrapper : ExcelCalcWrapper
    {
        public MultiFactorCalcWrapper()
        {
            CalcTask = Task.Run(() =>
            {
                TimeSpan timeSpan = TimeSpan.FromSeconds(1);
                UpdateProgress(0.0);
                Thread.Sleep(timeSpan);
                UpdateProgress(0.2);
                Thread.Sleep(timeSpan);
                UpdateProgress(0.4);
                Thread.Sleep(timeSpan);
                UpdateProgress(0.6);
                Thread.Sleep(timeSpan);
                UpdateProgress(0.8);
                Thread.Sleep(timeSpan);
                UpdateProgress(0.9);
                Thread.Sleep(timeSpan);
                UpdateProgress(1.0);
                base.Results = 12345.6789;
            });

        }

    }

    public static class MultiFactorXl
    {
        private static readonly Dictionary<string, MultiFactorCalcWrapper> _calcWrappers = new Dictionary<string, MultiFactorCalcWrapper>();

        [ExcelFunction(Name = AddIn.ExcelFunctionNamePrefix + nameof(StorageThreeFactor),
            Description = "Calculates the NPV, deltas, trigger prices and other metadata using a 3-factor seasonal model of price dynamics.",
            Category = AddIn.ExcelFunctionCategory, IsThreadSafe = false, IsVolatile = false, IsExceptionSafe = true)] // TODO turn IsThreadSafe to true and use ConcurrentDictionary?
        public static object StorageThreeFactor(string name)
        {
            return StorageExcelHelper.ExecuteExcelFunction(() =>
            {
                _calcWrappers[name] = new MultiFactorCalcWrapper();
                return name;
            });
        }

        [ExcelFunction(Name = AddIn.ExcelFunctionNamePrefix + nameof(SubscribeProgress),
            Description = "TODO.", // TODO
            Category = AddIn.ExcelFunctionCategory, IsThreadSafe = false, IsVolatile = false, IsExceptionSafe = true)] // TODO turn IsThreadSafe to true and use ConcurrentDictionary?
        public static object SubscribeProgress(string name)
        {
            return StorageExcelHelper.ExecuteExcelFunction(() =>
            {
                const string functionName = nameof(SubscribeProgress);
                return ExcelAsyncUtil.Observe(functionName, name, () =>
                {
                    MultiFactorCalcWrapper wrapper = _calcWrappers[name];
                    var excelObserver = new CalcWrapperProgressObservable(wrapper);
                    return excelObserver;
                });
            });
        }

        [ExcelFunction(Name = AddIn.ExcelFunctionNamePrefix + nameof(SubscribeStatus),
            Description = "TODO.", // TODO
            Category = AddIn.ExcelFunctionCategory, IsThreadSafe = false, IsVolatile = false, IsExceptionSafe = true)] // TODO turn IsThreadSafe to true and use ConcurrentDictionary?
        public static object SubscribeStatus(string name)
        {
            return StorageExcelHelper.ExecuteExcelFunction(() =>
            {
                const string functionName = nameof(SubscribeStatus);
                return ExcelAsyncUtil.Observe(functionName, name, () =>
                {
                    MultiFactorCalcWrapper wrapper = _calcWrappers[name];
                    var excelObserver = new CalcWrapperStatusObservable(wrapper);
                    return excelObserver;
                });
            });
        }

        [ExcelFunction(Name = AddIn.ExcelFunctionNamePrefix + nameof(SubscribeResult),
            Description = "TODO.", // TODO
            Category = AddIn.ExcelFunctionCategory, IsThreadSafe = false, IsVolatile = false, IsExceptionSafe = true)] // TODO turn IsThreadSafe to true and use ConcurrentDictionary?
        public static Task<object> SubscribeResult(string name)
        {
            return StorageExcelHelper.ExecuteExcelFunctionAsync(async () =>
            {
                MultiFactorCalcWrapper wrapper = _calcWrappers[name];
                return await wrapper.CalcTask.ContinueWith(task => wrapper.Results);
            });
        }

    }

    sealed class CalcWrapperStatusObservable : CalcWrapperObservableBase
    {
        public CalcWrapperStatusObservable(ExcelCalcWrapper calcWrapper) : base(calcWrapper)    
            => calcWrapper.CalcTask.ContinueWith(task => TaskStatusUpdate(task.Status));
        
        private void TaskStatusUpdate(TaskStatus taskStatus) => _observer?.OnNext(taskStatus.ToString("G"));
        
        protected override void OnSubscribe() => TaskStatusUpdate(_calcWrapper.CalcTask.Status);
    }

    sealed class CalcWrapperProgressObservable : CalcWrapperObservableBase
    {
        public CalcWrapperProgressObservable(ExcelCalcWrapper calcWrapper) : base(calcWrapper)
                    => _calcWrapper.OnProgressUpdate += ProgressUpdate;
        
        internal void ProgressUpdate(double progress) => _observer?.OnNext(progress);

        protected override void OnSubscribe() => _observer.OnNext(_calcWrapper.Progress);

        protected override void OnDispose()
        {
            _calcWrapper.OnProgressUpdate -= ProgressUpdate;
            base.OnDispose();
        }
    }

    abstract class CalcWrapperObservableBase : IExcelObservable
    {
        // TODO update ReSharper conventions for protected fields
        protected readonly ExcelCalcWrapper _calcWrapper;
        protected IExcelObserver _observer;

        protected CalcWrapperObservableBase(ExcelCalcWrapper calcWrapper)
        {
            _calcWrapper = calcWrapper;
            _calcWrapper.CalcTask.ContinueWith(task => _observer?.OnCompleted());
        }

        public IDisposable Subscribe(IExcelObserver excelObserver)
        {
            _observer = excelObserver;
            OnSubscribe();
            return new ActionDisposable(OnDispose);
        }

        protected abstract void OnSubscribe();
        
        protected virtual void OnDispose() => _observer = null;

    }

    class ActionDisposable : IDisposable
    {
        private readonly Action _disposeAction;
        public ActionDisposable(Action disposeAction)
        {
            _disposeAction = disposeAction;
        }
        public void Dispose()
        {
            _disposeAction();
        }
    }
}
