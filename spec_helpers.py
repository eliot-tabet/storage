
def generate_ratchets_with_maintenances(init_ratchet, maint_dates, storage_end c_inj, c_wit, maint = True, perc = False,
                                        inj_max = None, wit_max = None, max_wg = None):
    
    if not maint:
        return init_ratchet
    
    assert len(maint_dates) != 0, "maint_dates is empty"
    assert len(maint_dates) == len(c_inj) & len(c_wit) == len(c_inj), "c_inj, c_wit and maint_dates have different lengths"
    
    if perc:
        assert inj_max is not None
        assert wit_max is not None
        assert max_wg is not None
        
    from datetime import datetime, timedelta
    
    '''
    init_ratchet is a list defined as so:
    [
    (date_application1, [(working_gas1), (-max_wit1), (max_inj1)]),
    (date_application2, [(working_gas2), (-max_wit2), (max_inj2)]),
                .
                .
                .
    ]
    
    init_ratchet = [
                ('2021-04-01', # For days after 2021-04-01 (inclusive) until 2022-10-01 (exclusive):
                       [
                            (0.0, -150.0, 250.0),    # At min inventory of zero, max withdrawal of 150, max injection 250
                            (2000.0, -200.0, 175.0), # At inventory of 2000, max withdrawal of 200, max injection 175
                            (5000.0, -260.0, 155.0), # At inventory of 5000, max withdrawal of 260, max injection 155
                            (7000.0, -275.0, 132.0), # At max inventory of 7000, max withdrawal of 275, max injection 132
                        ]),
                  ('2022-10-01', # For days after 2022-10-01 (inclusive):
                       [
                            (0.0, -130.0, 260.0),    # At min inventory of zero, max withdrawal of 130, max injection 260
                            (2000.0, -190.0, 190.0), # At inventory of 2000, max withdrawal of 190, max injection 190
                            (5000.0, -230.0, 165.0), # At inventory of 5000, max withdrawal of 230, max injection 165
                            (7000.0, -245.0, 148.0), # At max inventory of 7000, max withdrawal of 245, max injection 148
                        ]),
                 ]
    maint_dates is a list of date strings : maint_dates = ["2021-06-01", "2022-06-05", "2022-11-01"]
    c_inj and c_wit are lists of floats : c_inj = [0.25, 0.2, 0.0], c_wit = [0.8, 0.5, 0.0]
    storage_end is a string : storage_end = '2022-12-01'
    inj_max, wit_max are in percentage - so already divided by 100
    max_wg is the max working volume or max inventory
    '''
    
    #maint_dates string list to 
    md = [datetime.strptime(maint_dates[i], "%Y-%m-%d") for i in range(len(maint_dates))]
    #acces init_ratchet dates
    rd = [datetime.strptime(init_ratchet[i][0], "%Y-%m-%d") for i in range(len(init_ratchet))]
    
    #merge 2 lists and sort ascending
    mrd = md + rd
    mrd.sort()
    
    #get indices of md and rd in mrd
    imd = [mrd.index(i) for i in md]
    ird = [mrd.index(i) for i in rd]
    
    assert imd[0] >= ird[0], "first maint_date before first ratchet date"
    
    #get the tupple of inj, wit of the first rd before the md
    m_ratchet = []
    for i in imd:
        inird = False
        j = i
        while not inird:
            j = j - 1
            if j in ird:
                #then apply x c_inj and x c_wit to the md date
                ll = rd.index(mrd[j]) 
                jratchet = init_ratchet[ll][1]
                nr = []
                for k in jratchet:
                    k = list(k)
                    l = md.index(mrd[i]) #index of the maint_date
                    k[1] = c_wit[l] * k[1]
                    k[2] = c_inj[l] * k[2]
                    k = tuple(k)
                    nr = nr + [k]
                m_ratchet = m_ratchet + [(maint_dates[l], nr)]
                #then get date after maint_date to apply the regular ratchet that was
                #before, unless next day new ratchet
                next_day = mrd[i] + timedelta(days=1)
                if next_day not in rd:
                    if mrd[i].strftime("%Y-%m-%d") != storage_end:
                        m_ratchet = m_ratchet + [(next_day.strftime("%Y-%m-%d"), jratchet)]
                inird = True
    
    #Finally add these all with init_ratchet, and order
    new_ratchet = init_ratchet + m_ratchet
    rd = [datetime.strptime(new_ratchet[i][0], "%Y-%m-%d") for i in range(len(new_ratchet))]
    new_ratchet = [x for _,x in sorted(zip(rd, new_ratchet))]
    
    if perc:
        for i in range(len(new_ratchet)):
            temp = new_ratchet[i][1]
            for j in range(len(temp)):
                k = temp[j]
                k = list(k)
                k[0] = k[0] * max_wg  #inventory
                k[1] = k[1] * wit_max #wit
                k[2] = k[2] * inj_max #inj
                k = tuple(k)
                temp[j] = k
    return new_ratchet


def generate_min_max_inventory_with_gates(storage_start = '2021-04-01', storage_end = '2022-04-01',
                                          max_wg = 100, gate_dates = ["2021-04-02", "2022-01-05"],
                                          gmin = [0.25, 0.2], gmax = [0.8, 0.5]):
    
    assert len(gate_dates) != 0, "gate_dates is empty"
    assert len(gate_dates) == len(gmin) & len(gmin) == len(gmax), "gmin, gmax and gate_dates have different lengths"
    
    #create a series with date as index
    #then change the values on gate day
    dr = pd.date_range(start = storage_start, end = storage_end).to_period("D")
    maxi = pd.Series([max_wg]*len(dr), dr)
    mini = pd.Series([0]*len(dr), dr)
    for i in range(len(gate_dates)):
        maxi.loc[pd.Period(gate_dates[i], 'D')] = gmax[i] * max_wg
        mini.loc[pd.Period(gate_dates[i], 'D')] = gmin[i] * max_wg
        
    return [mini, maxi]
    