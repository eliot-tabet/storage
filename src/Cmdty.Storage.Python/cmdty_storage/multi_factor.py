# Copyright(c) 2020 Jake Fowler
#
# Permission is hereby granted, free of charge, to any person
# obtaining a copy of this software and associated documentation
# files (the "Software"), to deal in the Software without
# restriction, including without limitation the rights to use, 
# copy, modify, merge, publish, distribute, sublicense, and/or sell
# copies of the Software, and to permit persons to whom the
# Software is furnished to do so, subject to the following
# conditions:
#
# The above copyright notice and this permission notice shall be
# included in all copies or substantial portions of the Software.
#
# THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, 
# EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
# OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
# NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
# HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, 
# WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
# FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
# OTHER DEALINGS IN THE SOFTWARE.

import clr
import System as dotnet
import System.Collections.Generic as dotnet_cols_gen
import pathlib as pl
clr.AddReference(str(pl.Path('cmdty_storage/lib/Cmdty.Core.Simulation')))
import Cmdty.Core.Simulation as net_sim
import pandas as pd
import numpy as np
from datetime import datetime, date
import typing as tp
from cmdty_storage import utils


class MultiFactorSpotSim:

    def __init__(self,
                 freq: str,
                 factors: tp.Iterable[tp.Tuple[float, utils.CurveType]],
                 factor_corrs: np.ndarray,
                 current_date: tp.Union[datetime, date],
                 fwd_curve: utils.CurveType,
                 sim_periods: tp.Iterable[tp.Union[pd.Period, datetime, date]],
                 seed: tp.Optional[int]=None
                 # time_func: Callable[[Union[datetime, date], Union[datetime, date]], float] TODO add this back in
                 ):

        if freq not in utils.FREQ_TO_PERIOD_TYPE:
            raise ValueError("freq parameter value of '{}' not supported. The allowable values can be found in the "
                             "keys of the dict curves.FREQ_TO_PERIOD_TYPE.".format(freq))

        time_period_type = utils.FREQ_TO_PERIOD_TYPE[freq]

        net_factors = dotnet_cols_gen.List[net_sim.MultiFactor.Factor[time_period_type]]()
        for mean_reversion, vol_curve in factors:
            net_vol_curve = utils.curve_to_net_dict(vol_curve, time_period_type)
            net_factors.Add(net_sim.MultiFactor.Factor[time_period_type](mean_reversion, net_vol_curve))

        net_factor_corrs = utils.as_net_array(factor_corrs)
        net_multi_factor_params = net_sim.MultiFactor.MultiFactorParameters[time_period_type](net_factors,
                                                                                              net_factor_corrs)
        #net_forward_curve = utils.curve_to_net_dict(fwd_curve, time_period_type)


    def simulate(self, num_sims: int) -> pd.DataFrame:
        pass
