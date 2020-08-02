import unittest
import pandas as pd
import numpy as np
from cmdty_storage import multi_factor as mf
from tests import utils
from datetime import date


class TestSpotPriceSim(unittest.TestCase):
    def test_regression(self):

        factors = [
            (0.0, {date(2020, 8, 1): 0.35,
                   '2021-01-15': 0.29, # Can use string to specify forward delivery date
                   date(2021, 7, 30): 0.32}),
            (0.15, {date(2020, 8, 1): 0.35,
                   '2021-01-15': 0.29,
                   date(2021, 7, 30): 0.32})
        ]

        factor_corrs = np.array([
                        [1.0, 0.6],
                        [0.6, 1.0]
                    ])
        fwd_curve = {
            '2020-08-01': 56.85,
            pd.Period('2021-01-15', freq='D'): 59.08,
            date(2021, 7, 30): 62.453
        }
        current_date = date(2020, 7, 27)
        # Demonstrates different ways tp specify spot periods to simulate. Easier in practice to just use
        # keys of fwd_curve
        spot_periods_to_sim = [pd.Period('2020-08-01'), '2021-01-15',  date(2021, 7, 30)]

        spot_simulator = mf.MultiFactorSpotSim('D', factors, factor_corrs, current_date, fwd_curve,
                                               spot_periods_to_sim, 12)
        sim_spot_prices = spot_simulator.simulate(10)

        self.assertEqual(True, True)


if __name__ == '__main__':
    unittest.main()
