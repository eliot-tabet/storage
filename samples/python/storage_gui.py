import pandas as pd
import ipywidgets as ipw
import ipysheet as ips
from cmdty_storage import CmdtyStorage, three_factor_seasonal_value, MultiFactorModel, multi_factor
from curves import max_smooth_interp, adjustments
from datetime import date, timedelta
from IPython.display import display
from ipywidgets.widgets.interaction import show_inline_matplotlib_plots
from collections import namedtuple
import itertools
import logging

# Set up logging
class OutputWidgetHandler(logging.Handler):
    """ Custom logging handler sending logs to an output widget """

    def __init__(self, *args, **kwargs):
        super(OutputWidgetHandler, self).__init__(*args, **kwargs)
        layout = {
            'width': '50%',
            'height': '160px',
            'border': '1px solid black',
            'overflow_y': 'auto',
        }
        self.out = ipw.Output(layout=layout)

    def emit(self, record):
        """ Overload of logging.Handler method """
        formatted_record = self.format(record)
        new_output = {
            'name': 'stdout',
            'output_type': 'stream',
            'text': formatted_record+'\n'
        }
        self.out.outputs = (new_output, ) + self.out.outputs 

    def clear_logs(self):
        """ Clear the current logs """
        self.out.clear_output()

logger = logging.getLogger('storage_gui')
log_handler = OutputWidgetHandler()
log_handler.setFormatter(logging.Formatter('%(asctime)s  - [%(levelname)s] %(message)s'))
logger.addHandler(log_handler)
logger.setLevel(logging.INFO)

log_level_wgt = ipw.Dropdown(description='Log Level',
                options=['Debug', 'Info', 'Warning', 'Error', 'Critical'],
                value='Info')

multi_factor.logger.addHandler(log_handler)
multi_factor.logger.setLevel(logging.INFO)

def on_log_level_change(change):
    level_text = change['new']
    level_int = getattr(logging, level_text.upper())
    logger.setLevel(level_int)
    multi_factor.logger.setLevel(level_int)
    
log_level_wgt.observe(on_log_level_change, names='value')

def on_clear_logs_clicked(b):
    log_handler.clear_logs()

btn_clear_logs = ipw.Button(description='Clear Log Display')
btn_clear_logs.on_click(on_clear_logs_clicked)

# Shared properties
freq='D'
num_fwd_rows = 15
date_format = 'YYYY-MM-DD'
num_ratch_rows = 20
RatchetRow = namedtuple('RatchetRow', ['date', 'inventory', 'inject_rate', 'withdraw_rate'])

def create_tab(titles, children):
    tab = ipw.Tab()
    for idx, title in enumerate(titles):
        tab.set_title(idx, title)
    tab.children = children
    return tab

def enumerate_ratchets():
    ratchet_row = 0
    while ratchet_row < num_ratch_rows and ratch_input_sheet[ratchet_row, 1].value != '':
        yield RatchetRow(ratch_input_sheet[ratchet_row, 0].value, ratch_input_sheet[ratchet_row, 1].value,
                        ratch_input_sheet[ratchet_row, 3].value, ratch_input_sheet[ratchet_row, 2].value)
        ratchet_row+=1

def read_ratchets():
    ratchets = []
    for ratch in enumerate_ratchets():
        if ratch.date != '':
            dt_item = (pd.Period(ratch.date, freq=freq), [(ratch.inventory, -ratch.inject_rate,
                                                        ratch.withdraw_rate)])
            ratchets.append(dt_item)
        else:
            dt_item[1].append((ratch.inventory, -ratch.inject_rate,
                                                        ratch.withdraw_rate))
    return ratchets

val_date_wgt = ipw.DatePicker(description='Val Date', value=date.today())
inventory_wgt = ipw.FloatText(description='Inventory')
ir_wgt = ipw.FloatText(description='Intrst Rate %', step=0.005)
discount_deltas_wgt = ipw.Checkbox(description='Discount Deltas', value=False)
val_inputs_wgt = ipw.VBox([val_date_wgt, inventory_wgt, ir_wgt, discount_deltas_wgt])

# Forward curve
fwd_input_sheet = ips.sheet(rows=num_fwd_rows, columns=2, column_headers=['fwd_start', 'price'])
for row_num in range(0, num_fwd_rows):
    ips.cell(row_num, 0, '', date_format=date_format, type='date')
    ips.cell(row_num, 1, '', type='numeric')

out_fwd_curve = ipw.Output()
smooth_curve_wgt = ipw.Checkbox(description='Apply Smoothing', value=False)
apply_wkend_shaping_wgt = ipw.Checkbox(description='Wkend Shaping', value=False, disabled=True)
wkend_factor_wgt = ipw.FloatText(description='Wkend shaping factor', step=0.005, disabled=True)
btw_plot_fwd_wgt = ipw.Button(description='Plot Forward Curve')

def on_smooth_curve_change(change):
    apply_wkend_shaping_wgt.disabled = not change['new']

smooth_curve_wgt.observe(on_smooth_curve_change, names='value')

def on_apply_wkend_shaping_change(change):
    wkend_factor_wgt.disabled = not change['new']

apply_wkend_shaping_wgt.observe(on_apply_wkend_shaping_change, names='value')

def on_plot_fwd_clicked(b):
    out_fwd_curve.clear_output()
    curve = read_fwd_curve()
    with out_fwd_curve:
        curve.plot()
        show_inline_matplotlib_plots()

btw_plot_fwd_wgt.on_click(on_plot_fwd_clicked)


fwd_data_wgt = ipw.HBox([ipw.VBox([smooth_curve_wgt, apply_wkend_shaping_wgt, wkend_factor_wgt,
                       fwd_input_sheet]), ipw.VBox([btw_plot_fwd_wgt, out_fwd_curve])])

# Common storage properties
stor_type_wgt = ipw.RadioButtons(options=['Simple', 'Ratchets'], description='Storage Type')
start_wgt = ipw.DatePicker(description='Start')
end_wgt = ipw.DatePicker(description='End')
inj_cost_wgt = ipw.FloatText(description='Injection Cost')
inj_consumed_wgt = ipw.FloatText(description='Inj % Consumed', step=0.001)
with_cost_wgt = ipw.FloatText(description='Withdrw Cost')
with_consumed_wgt = ipw.FloatText(description='With % Consumed', step=0.001)

storage_common_wgt = ipw.HBox([ipw.VBox([start_wgt, end_wgt, inj_cost_wgt, 
    with_cost_wgt]), ipw.VBox([stor_type_wgt, inj_consumed_wgt, with_consumed_wgt])])

# Simple storage type properties
invent_min_wgt = ipw.FloatText(description='Min Inventory')
invent_max_wgt = ipw.FloatText(description='Max Inventory')
inj_rate_wgt = ipw.FloatText(description='Injection Rate')
with_rate_wgt = ipw.FloatText(description='Withdrw Rate')
storage_simple_wgt = ipw.VBox([invent_min_wgt, invent_max_wgt, inj_rate_wgt, with_rate_wgt])

# Ratchet storage type properties

ratch_input_sheet = ips.sheet(rows=num_ratch_rows, columns=4, 
                              column_headers=['date', 'inventory', 'inject_rate', 'withdraw_rate'])
for row_num in range(0, num_ratch_rows):
    ips.cell(row_num, 0, '', date_format=date_format, type='date')
    ips.cell(row_num, 1, '', type='numeric')
    ips.cell(row_num, 2, '', type='numeric')
    ips.cell(row_num, 3, '', type='numeric')

# Compose storage
storage_details_wgt = ipw.VBox([storage_common_wgt, storage_simple_wgt])

def on_stor_type_change(change):
    if change['new'] == 'Simple':
        storage_details_wgt.children = (storage_common_wgt, storage_simple_wgt)
    else:
        storage_details_wgt.children = (storage_common_wgt, ratch_input_sheet)
stor_type_wgt.observe(on_stor_type_change, names='value')


# Volatility parameters
spot_vol_wgt = ipw.FloatText(description='Spot Vol', step=0.01)
spot_mr_wgt = ipw.FloatText(description='Spot Mean Rev', step=0.01)
lt_vol_wgt = ipw.FloatText(description='Long Term Vol', step=0.01)
seas_vol_wgt = ipw.FloatText(description='Seasonal Vol', step=0.01)
btn_plot_vol = ipw.Button(description='Plot Forward Vol')
out_vols =  ipw.Output()
vol_params_wgt = ipw.HBox([ipw.VBox([spot_vol_wgt, spot_mr_wgt, lt_vol_wgt, seas_vol_wgt, btn_plot_vol]), out_vols])

# Plotting vol
def btn_plot_vol_clicked(b):
    out_vols.clear_output()
    with out_vols:
        if val_date_wgt.value is None or end_wgt.value is None:
            print('Enter val date and storage end date.')
            return
        vol_model = MultiFactorModel.for_3_factor_seasonal(freq, spot_mr_wgt.value, spot_vol_wgt.value,
                                   lt_vol_wgt.value, seas_vol_wgt.value, val_date_wgt.value, end_wgt.value)
        start_vol_period = pd.Period(val_date_wgt.value, freq=freq)
        end_vol_period = start_vol_period + 1
        periods = pd.period_range(start=end_vol_period, end=end_wgt.value, freq=freq)
        fwd_vols = [vol_model.integrated_vol(start_vol_period, end_vol_period, period) for period in periods]
        fwd_vol_series = pd.Series(data=fwd_vols, index=periods)
        fwd_vol_series.plot()
        show_inline_matplotlib_plots()
        
btn_plot_vol.on_click(btn_plot_vol_clicked)

# Technical Parameters
num_sims_wgt = ipw.IntText(description='Num Sims', value=1000, step=500)
seed_is_random_wgt = ipw.Checkbox(description='Seed is Random', value=False)
random_seed_wgt = ipw.IntText(description='Seed', value=11)
grid_points_wgt = ipw.IntText(description='Grid Points', value=100, step=10)
basis_funcs_label_wgt = ipw.Label('Basis Functions')
basis_funcs_legend_wgt = ipw.VBox([ipw.Label('1=Constant'),
                                    ipw.Label('s=Spot Price'),
                                    ipw.Label('x_st=Short-term Factor'),
                                   ipw.Label('x_sw=Sum/Win Factor'),
                                   ipw.Label('x_lt=Long-term Factor')])

basis_funcs_input_wgt = ipw.Textarea(
    value='1 + x_st + x_sw + x_lt + x_st**2 + x_sw**2 + x_lt**2 + x_st**3 + x_sw**3 + x_lt**3',
    layout=ipw.Layout(width='95%', height='95%'))
basis_func_wgt = ipw.HBox([ipw.VBox([basis_funcs_label_wgt, basis_funcs_legend_wgt]), basis_funcs_input_wgt])
num_tol_wgt = ipw.FloatText(description='Numerical Tol', value=1E-10, step=1E-9)

def on_seed_is_random_change(change):
    if change['new']:
        random_seed_wgt.disabled = True
    else:
        random_seed_wgt.disabled = False

seed_is_random_wgt.observe(on_seed_is_random_change, names='value')

tech_params_wgt = ipw.HBox([ipw.VBox([num_sims_wgt, seed_is_random_wgt, random_seed_wgt, grid_points_wgt, 
                            num_tol_wgt]), basis_func_wgt])

tab_in_titles = ['Valuation Data', 'Forward Curve', 'Storage Details', 'Volatility Params', 'Technical Params']
tab_in_children = [val_inputs_wgt, fwd_data_wgt, storage_details_wgt, vol_params_wgt, tech_params_wgt]
tab_in = create_tab(tab_in_titles, tab_in_children)

# Output Widgets
progress_wgt = ipw.FloatProgress(min=0.0, max=1.0)
full_value_wgt = ipw.Text(description='Full Value', disabled=True)
intr_value_wgt = ipw.Text(description='Intr. Value', disabled=True)
extr_value_wgt = ipw.Text(description='Extr. Value', disabled=True)
value_wgts = [full_value_wgt, intr_value_wgt, extr_value_wgt]
values_wgt = ipw.VBox(value_wgts)

out_summary = ipw.Output()
summary_vbox = ipw.HBox([values_wgt, out_summary])

out_triggers = ipw.Output()

tab_out_titles = ['Summary', 'Trigger Prices']
tab_out_children = [summary_vbox, out_triggers]
tab_output = create_tab(tab_out_titles, tab_out_children)


def on_progress(progress):
    progress_wgt.value = progress

# Inputs Not Defined in GUI
def twentieth_of_next_month(period): return period.asfreq('M').asfreq('D', 'end') + 20

def read_fwd_curve():
    fwd_periods = []
    fwd_prices = []
    fwd_row = 0
    while fwd_input_sheet[fwd_row, 0].value != '':
        fwd_periods.append(pd.Period(fwd_input_sheet[fwd_row, 0].value, freq=freq))
        fwd_prices.append(fwd_input_sheet[fwd_row, 1].value)
        fwd_row += 1
    if smooth_curve_wgt.value:
        p1, p2 = itertools.tee(fwd_periods)
        next(p2, None)
        contracts = []
        for start, end, price in zip(p1, p2, fwd_prices):
            contracts.append((start, end - 1, price))
        weekend_adjust = None
        if apply_wkend_shaping_wgt.value:
            wkend_factor = wkend_factor_wgt.value
            weekend_adjust = adjustments.dayofweek(default=1.0, saturday=wkend_factor, sunday=wkend_factor)
        return max_smooth_interp(contracts, freq=freq, mult_season_adjust=weekend_adjust)
    else:
        return pd.Series(fwd_prices, pd.PeriodIndex(fwd_periods)).resample(freq).fillna('pad')

def btn_clicked(b):
    progress_wgt.value = 0.0
    for vw in value_wgts:
        vw.value = ''
    btn_calculate.disabled = True
    out_summary.clear_output()
    out_triggers.clear_output()
    try:
        global fwd_curve
        logger.debug('Reading forward curve.')
        fwd_curve = read_fwd_curve()
        logger.debug('Forward curve read successfully.')
        global storage
        global val_results_3f
        if stor_type_wgt.value == 'Simple':
            storage = CmdtyStorage(freq, storage_start=start_wgt.value, storage_end=end_wgt.value, 
                                   injection_cost=inj_cost_wgt.value, withdrawal_cost=with_cost_wgt.value,
                                  min_inventory=invent_min_wgt.value, max_inventory=invent_max_wgt.value,
                                  max_injection_rate=inj_rate_wgt.value, max_withdrawal_rate=with_rate_wgt.value,
                                  cmdty_consumed_inject=inj_consumed_wgt.value, 
                                  cmdty_consumed_withdraw=with_consumed_wgt.value)
        else:
            ratchets = read_ratchets()
            storage = CmdtyStorage(freq, storage_start=start_wgt.value, storage_end=end_wgt.value, 
                       injection_cost=inj_cost_wgt.value, withdrawal_cost=with_cost_wgt.value,
                       constraints=ratchets)

        interest_rate_curve = pd.Series(index=pd.period_range(val_date_wgt.value, 
                                  twentieth_of_next_month(pd.Period(end_wgt.value, freq='D')), freq='D'), dtype='float64')
        interest_rate_curve[:] = ir_wgt.value
        seed = None if seed_is_random_wgt.value else random_seed_wgt.value
        logger.info('Valuation started.')
        val_results_3f = three_factor_seasonal_value(storage, val_date_wgt.value, inventory_wgt.value, fwd_curve=fwd_curve,
                                     interest_rates=interest_rate_curve, settlement_rule=twentieth_of_next_month,
                                    spot_mean_reversion=spot_mr_wgt.value, spot_vol=spot_vol_wgt.value,
                                    long_term_vol=lt_vol_wgt.value, seasonal_vol=seas_vol_wgt.value,
                                    num_sims=num_sims_wgt.value, 
                                    basis_funcs=basis_funcs_input_wgt.value, discount_deltas = discount_deltas_wgt.value,
                                    seed=seed,
                                    num_inventory_grid_points=grid_points_wgt.value, on_progress_update=on_progress,
                                    numerical_tolerance=num_tol_wgt.value)
        logger.info('Valuation completed without error.')
        full_value_wgt.value = "{0:,.0f}".format(val_results_3f.npv)
        intr_value_wgt.value = "{0:,.0f}".format(val_results_3f.intrinsic_npv)
        extr_value_wgt.value = "{0:,.0f}".format(val_results_3f.extrinsic_npv)
        intr_delta = val_results_3f.intrinsic_profile['net_volume']
        
        active_fwd_curve = fwd_curve[storage.start:storage.end]
        with out_summary:
            ax_1 = val_results_3f.deltas.plot(legend=True)
            ax_1.set_ylabel('Delta')
            intr_delta.plot(legend=True, ax=ax_1)
            ax_2 = active_fwd_curve.plot(secondary_y=True, legend=True, ax=ax_1)
            ax_2.set_ylabel('Forward Price')
            ax_1.legend(['Full Delta', 'Intrinsic Delta'])
            ax_2.legend(['Forward Curve'])
            show_inline_matplotlib_plots()
        with out_triggers:
            trigger_prices = val_results_3f.trigger_prices
            ax_1 = trigger_prices['inject_trigger_price'].plot(legend=True)
            ax_1.set_ylabel('Price')
            trigger_prices['withdraw_trigger_price'].plot(legend=True, ax=ax_1)
            active_fwd_curve.plot(legend=True, ax=ax_1)
            ax_2 = val_results_3f.expected_profile['inventory'].plot(secondary_y=True, legend=True, ax=ax_1)
            ax_2.set_ylabel('Volume')
            box = ax_1.get_position()
            ax_1.set_position([box.x0, box.y0 + box.height * 0.1, box.width, box.height * 0.9])
            ax_1.legend(['Inject Trigger Price', 'Withdraw Trigger Price', 'Forward Curve'], loc='upper center', 
                        bbox_to_anchor=(0.2, -0.12))
            ax_2.legend(['Expected Inventory'], loc='upper center', bbox_to_anchor=(0.7, -0.12))
            show_inline_matplotlib_plots()
    except Exception as e:
        logger.error('Exception:')
        logger.error(e)
    finally:
        btn_calculate.disabled = False


btn_calculate = ipw.Button(description='Calculate')
btn_calculate.on_click(btn_clicked)  

controls_wgt = ipw.HBox([ipw.VBox([btn_calculate, progress_wgt, log_level_wgt, btn_clear_logs]), log_handler.out])

def display_gui():
    display(tab_in)
    display(controls_wgt)
    display(tab_output)

def test_data_btn():
    def btn_test_data_clicked(b):
        today = date.today()
        inventory_wgt.value = 1456
        start_wgt.value = today + timedelta(days=5)
        end_wgt.value = today + timedelta(days=380)
        invent_max_wgt.value = 100000
        wkend_factor_wgt.value = 0.97
        inj_rate_wgt.value = 260
        with_rate_wgt.value = 130
        inj_cost_wgt.value = 1.1
        inj_consumed_wgt.value = 0.01
        with_cost_wgt.value = 1.3
        with_consumed_wgt.value = 0.018
        ir_wgt.value = 0.005
        spot_vol_wgt.value = 1.23
        spot_mr_wgt.value = 14.5
        lt_vol_wgt.value = 0.23
        seas_vol_wgt.value = 0.39
        for idx, price in enumerate([58.89, 61.41, 62.58, 58.9, 43.7, 58.65, 61.45, 56.87]):
            fwd_input_sheet[idx, 1].value = price
        for idx, do in enumerate([0, 30, 60, 90, 150, 250, 350, 400]):
            fwd_input_sheet[idx, 0].value = (today + timedelta(days=do)).strftime('%Y-%m-%d')
        # Populate ratchets
        ratch_input_sheet[0, 0].value = today.strftime('%Y-%m-%d')
        for idx, inv in enumerate([0.0, 25000.0, 50000.0, 60000.0, 65000.0]):
            ratch_input_sheet[idx, 1].value = inv
        for idx, inj in enumerate([650.0, 552.5, 512.8, 498.6, 480.0]):
            ratch_input_sheet[idx, 2].value = inj
        for idx, wthd in enumerate([702.7, 785.0, 790.6, 825.6, 850.4]):
            ratch_input_sheet[idx, 3].value = wthd
        ratch_2_offset = 5
        ratch_input_sheet[ratch_2_offset, 0].value = (today + timedelta(days = 150)).strftime('%Y-%m-%d')
        for idx, inv in enumerate([0.0, 24000.0, 48000.0, 61000.0, 65000.0]):
            ratch_input_sheet[ratch_2_offset + idx, 1].value = inv
        for idx, inj in enumerate([645.8, 593.65, 568.55, 560.8, 550.0]):
            ratch_input_sheet[ratch_2_offset + idx, 2].value = inj
        for idx, wthd in enumerate([752.5, 813.7, 836.45, 854.78, 872.9]):
            ratch_input_sheet[ratch_2_offset + idx, 3].value = wthd

    btn_test_data = ipw.Button(description='Populate Test Data')
    btn_test_data.on_click(btn_test_data_clicked)

    display(btn_test_data)