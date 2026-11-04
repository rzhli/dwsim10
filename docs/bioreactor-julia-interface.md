# BioReactor Julia interface v1

The selected Script Manager script is evaluated in a module in the flowsheet's persistent Julia session. Modules are cached by source and working directory; editing the source loads a new module. Relative includes resolve next to the flowsheet. Define:

```julia
function run_bioreactor(context)
    # Integrate your model and map its inventories or rates to process streams.
    return result
end
```

`context` is a `Dict{String,Any}`. No JSON Julia package is required by the transport. The model may use installed packages in the Julia environment used to launch DWSIM, or explicitly resolve its own project before importing dependencies. The ABE model locates its bundled `generic-julia/julia-env` directory relative to the flowsheet, temporarily prepends it to `LOAD_PATH` for imports, and restores `LOAD_PATH` in a `finally` block. This supports ordinary DWSIM launches without changing the active project for other scripts. Keep the bundled project with the flowsheet when moving it.

## Context

| Key | Meaning |
|---|---|
| `schema_version` | `1` |
| `operation.mode` | `Batch`, `FedBatch` or `Continuous` |
| `operation.volume_m3` | Working/initial culture volume |
| `operation.duration_s` | Requested integration duration |
| `operation.thermal_mode` | `Isothermal`, `Adiabatic` or `DefinedOutletTemperature` |
| `operation.outlet_temperature_K` | Defined outlet temperature setpoint |
| `operation.pressure_drop_Pa` | Pressure-drop setting |
| `operation.aerobic` | Aerobic flag |
| `operation.use_feed_schedule` | Whether common feed pulses are enabled |
| `operation.feed_pulses` | Array of `start_s`, `volume_m3`, `rate_m3_s` |
| `operation.steady_tolerance` | Requested steady-state tolerance, interpreted by the model |
| `operation.max_integration_time_s` | Continuous-mode horizon; zero means model default |
| `parameters` | Model Settings JSON object, with model-defined names and units |
| `inlets` | Connected material inlets |
| `compounds` | Selected compound names mapped to `{molar_mass_kg_kmol}` |
| `connected_outlet_ports` | Zero-based connected material outlet indices |

Each inlet contains `port`, `temperature_K`, `pressure_Pa`, `mass_flow_kg_s`, `volume_flow_m3_s`, and `mass_flows_kg_s` (a dictionary keyed by the exact selected compound names). Volumetric flow comes from the thermodynamic stream calculation; a kinetic model may require a separately specified reference density or volume.

All operation settings are requests to the model, not equations imposed by the bridge. Reject unsupported settings explicitly. The generic adapter does not impose a particular concentration basis, organism, substrate, gas allocation, pH or kinetic solver.

The editor's explicit cycle-average input buttons recognize optional numeric model settings `initial_density_kg_m3` and `feed_density_kg_m3`. When present, the corresponding button uses `reference density × specified charge/feed volume ÷ fermentation duration` as the required mass flow, preserving composition. Missing keys retain the thermodynamic-volume convention. These convenience actions do not add equations to a model or change the generic calculation protocol. For Batch/FedBatch, the editor labels the integration horizon `Fermentation Duration`; after changing it, reapply both input averages for a fed-batch model.

## Result

Return a dictionary with string keys or a named tuple:

```julia
(
    schema_version = 1,
    model_name = "My model",
    time_s = times,                # at least two finite times, starting at 0
    series = [
        (id="state_a", name="State A", unit="mmol/L",
         group="Concentrations", values=state_a),
    ],
    scalars = [                   # optional
        (id="yield_a", name="Yield", unit="g/g", value=yield_a),
    ],
    outlets = [
        (port=0, temperature_K=T, pressure_Pa=P,
         mass_flows_kg_s=Dict("Water"=>water_flow, "Ethanol"=>ethanol_flow)),
        # Include every other connected material outlet, even at zero flow.
    ],
    heat_duty_W = nothing,        # positive INTO reactor; see below
    mass_accumulation_kg_s = 0.0, # optional, positive accumulation
    notes = "Model assumptions and applicability."
)
```

Time is strictly increasing. In Batch and FedBatch, the last point must equal the requested duration to within 0.001 s. All series must have one finite value per time. States or rates may be negative if meaningful to the model. Units are descriptive and preserved; the interface does not silently convert model results. Chart groups must contain series with identical units.

Series and scalar IDs must be unique across both lists, begin with a letter, and contain only letters, digits or underscores. `group` defaults to `Model states`. Use different groups for different units. Final values are exposed through the native property API as `Julia Result: <id>` and in native reports/results. All series persist in `JuliaGenericResultJSON`; they are restored on reopening and can be exported to CSV.

Return each connected outlet exactly once, with positive temperature and pressure and finite nonnegative component flows. Omitted selected compounds have zero outlet flow. Unselected compound names are rejected. The adapter checks `sum(inlets) - sum(outlets) - mass_accumulation_kg_s = 0`, within `1e-8 * max(in,out) + 1e-12 kg/s`, before writing any outlet. It never allocates missing mass to CO₂ or biomass.

For batch flowsheets, explicitly define a cycle-average stream convention, as the ABE example does. For a changing vessel inventory, explicitly return the accumulation rate appropriate to the same averaging interval. The interface checks total mass, not elemental balances or physical validity of a supplied kinetic model.

`heat_duty_W` is optional or `nothing` only when no energy stream is connected. With an energy stream, return a finite heat duty. DWSIM's outgoing energy connector is assigned the opposite sign. No paper heat prediction is synthesized from missing model data. Outlet streams receive the reactor property package and a temperature/pressure specification for downstream calculation.

The legacy `run_bioreactor(initial, duration_h, output_path, ...)` and fixed-column CSV interface have been removed. Existing legacy scripts must explicitly adapt their states and material/energy mapping to v1.
