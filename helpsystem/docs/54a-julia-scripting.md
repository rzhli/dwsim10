# Julia scripts in the Avalonia Script Manager

Open a simulation, then choose **Tools > Script Manager**. Create a script and select
**Julia** in **Interpreter**. The code in the editor is executed directly by your local
Julia executable, without Python. **Highlighting** only selects the editor's syntax colours.

The script text and interpreter are saved in the simulation.
Both **Run** and **Run Async** execute off the UI thread and keep the output capture active
until that script finishes. Standard output and errors appear in the Output pane.

## Default execution environment

DWSIM automatically searches `PATH`, then the current user's `.juliaup/bin`
installation, and starts Julia with its default package environment. There are no
per-script executable or project settings. Previously saved path overrides are ignored.
DWSIM does not install Julia or Julia packages automatically.

Each run starts in the saved simulation's directory, or the application's current
directory for an unsaved simulation. Julia starts with
`--startup-file=no --history-file=no --color=no`.

## Persistent sessions

The first run starts one Julia process for the flowsheet. Later runs reuse that process,
including loaded packages, compiled methods and global variables. All Julia scripts in
the same flowsheet share this session and run one at a time; different flowsheets have
separate sessions. The Output pane reports the process ID when starting or reusing Julia.

An ordinary Julia exception marks the run as failed and leaves the session available.
The failure message includes the current run's Julia stderr (up to the last 32,768
characters), so BioReactor calculation errors also show the underlying Julia exception
without requiring the informational output log.
**Stop** terminates the session and its child processes, so the next run starts a fresh
session. Closing the manager cancels an active run but preserves an idle session.
Closing the flowsheet or DWSIM releases its session. If Julia exits unexpectedly, the
next run also starts a new session. Session variables are not saved in the simulation file.

Use **Insert Snippet** for Julia examples, or include a model saved beside the simulation:

```julia
include(joinpath(pwd(), "model.jl"))
```

The manager evaluates the script in Julia's `Main` module with a virtual filename beside
the simulation, so `pwd()`, `@__DIR__` and relative `include` calls resolve there.
Within an included `model.jl`, normal Julia `@__DIR__` and relative `include` behaviour apply.

## Relation to Python and flowsheet calculations

IronPython executes Python inside DWSIM's .NET process and can access the injected
`Flowsheet` object directly. Python.NET embeds a configured CPython runtime in the same
process, allowing Python packages such as NumPy and SciPy to operate alongside the
DWSIM .NET object API.

Julia execution uses a separate local process. It does not inject live `Flowsheet`,
`Solver` or stream objects, and printing a value does not modify a stream. Julia can
perform numerical modelling and read/write model data files. The BioReactor adapter
below applies its returned concentration trajectory to material outlets.

Event-linked scripts reuse the same Julia session. The Python Script unit operation
remains a separate interface; selecting Julia in the Script Manager does not convert it.

## BioReactor user scripts

For either an existing or a newly added BioReactor, select **Kinetic Model > User Script**,
then **Language > Julia**, and choose the script stored in that flowsheet. Create or edit
the source in **Tools > Script Manager**, with its interpreter set to **Julia**.
The old `JuliaCoFermentation` enum value is retained in saved files and automation for
compatibility; the editor displays it as **User Script / Julia**.

The two language options currently have different contracts:

- **Python (IronPython)** sets `mu` in 1/s. DWSIM integrates its existing single-substrate
  balances with the reactor's growth yields and other kinetic settings.
- **Julia** integrates the complete five-species model, with volume as an additional
  state in fed-batch operation. The current entry point is
  `run_bioreactor(initial, duration_h, output_path, operation)`. `initial` contains biomass,
  glucose, xylose, ethanol and xylitol concentrations in g/L. Legacy three-argument
  scripts remain supported for Batch only. Write a numeric CSV to `output_path`:

  ```text
  time_h,biomass_g_L,glucose_g_L,xylose_g_L,ethanol_g_L,xylitol_g_L,mu_h,q_glucose_h,q_xylose_h,q_ethanol_h
  ```

The Julia script is loaded into its own module inside the persistent flowsheet session.
Its internal module name is unrestricted. Unchanged source reuses its loaded function;
editing the script loads the new source on the next reactor calculation.

`operation` is a Julia named tuple:

```julia
(mode = :fed_batch, volume_m3 = 20.0, duration_h = 24.0,
 feed_flow_m3_h = 10.0 / 24.0, feed_g_L = [0.0, 27.0, 32.0, 0.0, 0.0])
```

Modes are `:batch`, `:fed_batch` and `:continuous`. All flows and concentrations supplied
to Julia use the reactor's 30 C broth-volume basis. The `feed_g_L` species order matches
`initial`. The adapter retains the anaerobic, fixed-temperature, controlled-pH-5 scope
of the supplied Model C. Other state vectors or temperature models need an extension.

### Batch

The first inlet represents the initial charge averaged over the batch duration. Its
flow must equal `volume / duration`. The CSV time column must increase from zero to
`duration_h`. The ten-column legacy CSV above is accepted; an optional final `volume_m3`
column must stay constant. The second inlet is unused and must be disconnected.

Change **Calculation Parameters > Batch Duration** to set the fermentation interval.
For 30 m3, shortening 60 h to 24 h requires changing the initial-charge stream from
0.5 to 1.25 m3/h, preserving composition and initial concentrations.

### Fed-batch: a Julia feed curve

**Initial Working Volume** specifies the initial broth volume. The first inlet again
represents the initial charge averaged over **Fed-batch Duration**. The second inlet,
**Supplemental Feed (Cycle Average)**, supplies fixed feed composition and the average
feed flow over that same interval. Leaving it disconnected represents zero supplementation.

The supplied script exposes this editable example:

```julia
function feed_rate_m3_h(time_h, operation)
    2.0 * operation.feed_flow_m3_h * time_h / operation.duration_h
end
feed_switch_times_h(operation) = Float64[]
```

The example ramps from zero to twice the average feed flow. For a piecewise curve,
list its discontinuity times in `feed_switch_times_h` so the ODE solver lands on each
change. Negative or non-finite feed rates are rejected. The balances are:

```text
dC/dt = reaction(C) + F(t)/V * (Cfeed - C)
dV/dt = F(t)
```

Append `,volume_m3` to the CSV header and supply volume on every row. DWSIM requires
positive, nondecreasing volume and checks that the integrated feed equals the second
stream's average flow multiplied by the duration. A mismatch gives an error with the
required average flow; the adapter does not rescale the curve or the stream.
The outlet represents final inventory divided by the cycle duration.

This feeding example is an extension of the paper's rate equations, not a validated
feeding protocol from the paper. Feed composition changes, bleed and cell retention
are not part of this interface.

### Continuous

The first inlet now represents actual continuous feed. Combine multiple feeds upstream;
leave the second inlet disconnected. Julia applies `dC/dt = reaction(C) + D*(Cfeed-C)`
with fixed volume and `D = inlet flow / volume`. **Initial Culture Biomass** specifies
startup inoculum separately from biomass in the feed. Other initial concentrations
come from the inlet.

**Batch Duration** is unused and hidden. Two properties control the steady-state run
instead. **Steady-state Tolerance** (default `1e-8`) is the normalized species-balance
residual at which the script stops; the adapter passes it as `operation.steady_tolerance`,
and scripts written before the field keep their own `1e-8` (the adapter warns when the
property is changed but the script ignores it). **Max Integration Time** caps the run;
zero selects `max(200 h, 40 * hydraulic residence time)`. Reaching the cap without meeting
the tolerance raises an error. Neither is the culture residence time: `1e-8` needs about
20 residence times, `1e-5` stops much earlier and moves the steady state only in the
sixth significant digit. The report and the **Integrated Time** result show where the
run stopped.

If the error says `CSTR steady state was not reached within the integration horizon`,
raise **Max Integration Time**, or set it to **0** to restore the automatic horizon.
Do not use this property to request a finite-duration outlet: Continuous returns a
steady-state outlet, not the transient state at that time. For example, the supplied
30 m3, 3 m3/h case with tolerance `1e-5` needs about 123 h of integration; a 30 h cap
fails even though its hydraulic residence time is only 10 h.

The CSV includes `volume_m3`, starts at zero and ends at the computed steady state.
Results show hydraulic residence time and dilution rate. A zero inoculum with no
biomass in the feed permits only the no-growth solution.

### Results and limitations

The five concentrations come from the selected script. Off-gas mass and duty are
approximate balance closures, not independently validated gas/heat predictions.
The **Co-Fermentation** chart places sugars and biomass on the left axis and ethanol and
xylitol on the right axis; both axes display g/L and scale independently. Trajectory
export includes culture volume in m3. Thermodynamic outlet density can differ from
the broth-volume basis used by the kinetic model.
