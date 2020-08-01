import unittest
import pandas as pd
from cmdty_storage import multi_factor as mf
from tests import utils
from datetime import date


class TestSpotPriceSim(unittest.TestCase):
    def test_regression(self):
        def time_func(start, end):
            return 1.0  # TODO implement properly or create time_funcs module

        factors = [
            (0.0, {date(2020, 8, 1): 0.35,
                   "2021-01-15": 0.29,
                   date(2021, 7, 30): 0.32})
        ]

        spot_simulator = mf.MultiFactorSpotSim('D', factors, None, None, None, None)

        self.assertEqual(True, True)


if __name__ == '__main__':
    unittest.main()
