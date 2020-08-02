import unittest
import pandas as pd
import numpy as np
from cmdty_storage import multi_factor as mf
from tests import utils
from datetime import date


class TestSpotPriceSim(unittest.TestCase):
    def test_regression(self):
        factors = [ # Tuples where 1st element is factor mean-reversion, 2nd element is factor vol curve
            (0.0, {date(2020, 8, 1): 0.35,
                   '2021-01-15': 0.29,  # Can use string to specify forward delivery date
                   date(2021, 7, 30): 0.32}),
            (2.5, {date(2020, 8, 1): 0.15,
                   '2021-01-15': 0.18,
                   date(2021, 7, 30): 0.21}),
            (16.2, {date(2020, 8, 1): 0.95,
                   '2021-01-15': 0.92,
                   date(2021, 7, 30): 0.89}),
        ]

        factor_corrs = np.array([
            [1.0, 0.6, 0.3],
            [0.6, 1.0, 0.4],
            [0.3, 0.4, 1.0]
        ])
        fwd_curve = {
            '2020-08-01': 56.85,
            pd.Period('2021-01-15', freq='D'): 59.08,
            date(2021, 7, 30): 62.453
        }
        current_date = date(2020, 7, 27)
        # Demonstrates different ways tp specify spot periods to simulate. Easier in practice to just use
        # keys of fwd_curve
        spot_periods_to_sim = [pd.Period('2020-08-01'), '2021-01-15', date(2021, 7, 30)]

        spot_simulator = mf.MultiFactorSpotSim('D', factors, factor_corrs, current_date, fwd_curve,
                                               spot_periods_to_sim, 12)
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


if __name__ == '__main__':
    unittest.main()
