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

import unittest
import pandas as pd
import numpy as np
from cmdty_storage import multi_factor as mf
from datetime import date
import itertools


class TestSpotPriceSim(unittest.TestCase):
    def test_regression(self):
        factors = [ # Tuples where 1st element is factor mean-reversion, 2nd element is factor vol curve
            (0.0, {date(2020, 8, 1): 0.35,
                   '2021-01-15': 0.29,  # Can use string to specify forward delivery date
                   date(2021, 7, 30): 0.32}),
            # factor vol can also be specified as a pandas Series
            (2.5, pd.Series(data=[0.15, 0.18, 0.21],
                            index=pd.PeriodIndex(data=['2020-08-01', '2021-01-15', '2021-07-30'], freq='D'))),
            (16.2, {date(2020, 8, 1): 0.95,
                   '2021-01-15': 0.92,
                   date(2021, 7, 30): 0.89}),
        ]

        factor_corrs = np.array([
            [1.0, 0.6, 0.3],
            [0.6, 1.0, 0.4],
            [0.3, 0.4, 1.0]
        ])

        # Like with factor vol, the fwd_curve can be a pandas Series object
        fwd_curve = {
            '2020-08-01': 56.85,
            pd.Period('2021-01-15', freq='D'): 59.08,
            date(2021, 7, 30): 62.453
        }
        current_date = date(2020, 7, 27)
        # Demonstrates different ways tp specify spot periods to simulate. Easier in practice to just use
        # keys of fwd_curve
        spot_periods_to_sim = [pd.Period('2020-08-01'), '2021-01-15', date(2021, 7, 30)]

        random_seed = 12
        spot_simulator = mf.MultiFactorSpotSim('D', factors, factor_corrs, current_date, fwd_curve,
                                               spot_periods_to_sim, random_seed)
        num_sims = 4
        sim_spot_prices = spot_simulator.simulate(num_sims)
        self.assertEqual(3, len(sim_spot_prices))

        sim1 = sim_spot_prices[0]
        self.assertEqual(52.599763976889733, sim1['2020-08-01'])
        self.assertEqual(57.559631642935514, sim1['2021-01-15'])
        self.assertEqual(89.405269927726337, sim1['2021-07-30'])

        sim2 = sim_spot_prices[1]
        self.assertEqual(46.1206448628463, sim2['2020-08-01'])
        self.assertEqual(72.0381089486175, sim2['2021-01-15'])
        self.assertEqual(85.188698031173786, sim2['2021-07-30'])

        sim3 = sim_spot_prices[2]
        self.assertEqual(58.158385806825891, sim3['2020-08-01'])
        self.assertEqual(82.496071735623431, sim3['2021-01-15'])
        self.assertEqual(138.68587285875978, sim3['2021-07-30'])

        sim4 = sim_spot_prices[3]
        self.assertEqual(65.500441945042979, sim4['2020-08-01'])
        self.assertEqual(42.812676607997183, sim4['2021-01-15'])
        self.assertEqual(76.586790647813046, sim4['2021-07-30'])


class TestMultiFactorModel(unittest.TestCase):
    _short_plus_long_indices = pd.period_range(start='2020-09-01', periods=25, freq='D') \
                                             .append(pd.period_range(start='2030-09-01', periods=25, freq='D'))
    _1f_0_mr_model = mf.MultiFactorModel('D', [(0.0, {'2020-09-01': 0.36, '2020-10-01': 0.29, '2020-11-01': 0.23})])
    _1f_pos_mr_model = mf.MultiFactorModel('D', [(2.5, pd.Series(data=np.linspace(0.65, 0.38, num=50),
                                            index=_short_plus_long_indices))])
    _2f_canonical_model = mf.MultiFactorModel('D',
                      factors=[(0.0, pd.Series(data=np.linspace(0.53, 0.487, num=50), index=_short_plus_long_indices)),
                            (2.5, pd.Series(data=np.linspace(1.45, 1.065, num=50), index=_short_plus_long_indices))],
                      factor_corrs=0.87) # If only 2 factors can supply a float for factor_corrs rather than a matrix

    def test_single_non_mean_reverting_factor_implied_vol_equals_factor_vol(self):
        fwd_contract = '2020-09-01'
        implied_vol = self._1f_0_mr_model.implied_vol(date(2020, 8, 5), date(2020, 8, 30), '2020-09-01')
        factor_vol = self._1f_0_mr_model._factors[0][1][fwd_contract]
        self.assertEqual(factor_vol, implied_vol)

    def test_single_non_mean_reverting_factor_correlations_equal_one(self):
        self._assert_cross_correlations_all_one(date(2020, 8, 1), date(2020, 9, 1), self._1f_0_mr_model)

    def test_single_mean_reverting_factor_correlations_equal_one(self):
        self._assert_cross_correlations_all_one(date(2020, 5, 1), date(2020, 9, 1), self._1f_pos_mr_model)

    def _assert_cross_correlations_all_one(self, obs_start, obs_end, model: mf.MultiFactorModel):
        fwd_points = model._factors[0][1].keys()
        for fwd_point_1, fwd_point_2 in itertools.product(fwd_points, fwd_points):
            if fwd_point_1 != fwd_point_2:
                corr = model.integrated_correlation(obs_start, obs_end, fwd_point_1, fwd_point_2)
                self.assertAlmostEqual(1.0, corr, places=14)

    def test_single_mean_reverting_factor_variance_far_in_future_equals_zero(self):
        variance = self._1f_pos_mr_model.variance('2020-08-05', '2020-09-01', fwd_contract='2030-09-15')
        self.assertAlmostEquals(0.0, variance, places=14)

    def test_2f_canonical_vol_far_in_future_equal_non_mr_vol(self):
        fwd_contract = '2030-09-15'
        implied_vol = self._2f_canonical_model.implied_vol('2020-08-05', '2021-08-05', fwd_contract)
        non_mr_factor_vol = self._2f_canonical_model._factors[0][1][fwd_contract]
        self.assertAlmostEquals(non_mr_factor_vol, implied_vol, places=10)


if __name__ == '__main__':
    unittest.main()
