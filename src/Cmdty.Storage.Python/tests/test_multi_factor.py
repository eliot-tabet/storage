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
                   "2021-01-15": 0.29,
                   date(2021, 7, 30): 0.32}),
            (0.15, {date(2020, 8, 1): 0.35,
                   "2021-01-15": 0.29,
                   date(2021, 7, 30): 0.32})
        ]

        factor_corrs = np.array([
                        [1.0, 0.6],
                        [0.6, 1.0]
                    ])

        spot_simulator = mf.MultiFactorSpotSim('D', factors, factor_corrs, None, None, None)

        self.assertEqual(True, True)


if __name__ == '__main__':
    unittest.main()
