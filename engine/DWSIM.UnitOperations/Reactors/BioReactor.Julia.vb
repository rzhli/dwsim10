Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Security.Cryptography
Imports System.Text
Imports DWSIM.Interfaces
Imports DWSIM.Interfaces.Enums
Imports DWSIM.SharedClasses.Scripting
Imports DWSIM.Thermodynamics.Streams

Namespace Reactors

    Public Partial Class Reactor_BioReactor

        Public Property JuliaModelScriptName As String = ""
        Public Property XylitolCompound As String = ""
        Public Property Result_Xylose_gL As Double
        Public Property Result_Xylitol_gL As Double
        Public Property Result_JuliaClosureGas_kg_s As Double
        Public Property JuliaInitialBiomass_gL As Double = 0.01
        <System.Xml.Serialization.XmlIgnore, Newtonsoft.Json.JsonIgnore>
        Public Property Result_JuliaVolume_m3 As Double
            Get
                Return Result_WorkingVolume_m3
            End Get
            Set(value As Double)
                Result_WorkingVolume_m3 = value
            End Set
        End Property
        Public Property Result_JuliaResidenceTime_s As Double
        Public Property Result_JuliaDilutionRate_h As Double
        ''' <summary>Scaled CSTR balance residual at which a continuous Julia run stops; passed to the script as operation.steady_tolerance.</summary>
        Public Property JuliaSteadyStateTolerance As Double = 0.00000001
        ''' <summary>Upper limit of the continuous integration horizon (s). Zero selects max(200 h, 40 x hydraulic residence time).</summary>
        Public Property JuliaMaxIntegrationTime_s As Double = 0.0
        ''' <summary>Time at which the Julia trajectory ended (s): the duration in batch/fed-batch, the steady-state stop in continuous mode.</summary>
        Public Property Result_JuliaIntegrationTime_s As Double
        ''' <summary>
        ''' Runs Julia batch, variable-volume fed-batch or steady-state CSTR balances.
        ''' Batch charge and fed-batch feed streams carry cycle-average mass flows.
        ''' The paper does not provide a complete
        ''' nutrient/elemental or heat model. Untracked product mass is represented by CO2
        ''' on the off-gas port; it is an explicitly approximate mass closure, not a fitted CER.
        ''' </summary>
        Private Sub CalculateJuliaCoFermentation()
            Dim continuous = OperatingMode = BioReactorMode.Continuous
            Dim fedBatch = OperatingMode = BioReactorMode.FedBatch
            If Not [Enum].IsDefined(GetType(BioReactorMode), OperatingMode) Then Throw New InvalidOperationException("Select a valid operating mode.")
            If IsAerobic Then Throw New InvalidOperationException("The Z. mobilis model is anaerobic; disable Aerobic.")
            If Not Double.IsFinite(Volume) OrElse Volume <= 0.0 Then
                Throw New InvalidOperationException("Julia co-fermentation requires a positive initial/working volume.")
            End If
            If Not continuous AndAlso (Not Double.IsFinite(BatchDuration) OrElse BatchDuration <= 0.0) Then
                Throw New InvalidOperationException("Batch and fed-batch operation require a positive fermentation duration.")
            End If
            If continuous AndAlso (Not Double.IsFinite(JuliaInitialBiomass_gL) OrElse JuliaInitialBiomass_gL < 0.0) Then
                Throw New InvalidOperationException("Initial culture biomass must be finite and nonnegative.")
            End If
            If continuous AndAlso (Not Double.IsFinite(JuliaSteadyStateTolerance) OrElse JuliaSteadyStateTolerance <= 0.0) Then
                Throw New InvalidOperationException("Steady-state tolerance must be a positive scaled balance residual (default 1e-8).")
            End If
            If continuous AndAlso (Not Double.IsFinite(JuliaMaxIntegrationTime_s) OrElse JuliaMaxIntegrationTime_s < 0.0) Then
                Throw New InvalidOperationException("Max integration time must be zero (automatic) or a positive time.")
            End If
            If Not GraphicObject.InputConnectors(0).IsAttached OrElse
               Not GraphicObject.OutputConnectors(0).IsAttached OrElse
               Not GraphicObject.OutputConnectors(1).IsAttached Then
                Throw New InvalidOperationException("Connect the feed, broth and off-gas streams before running Julia co-fermentation.")
            End If
            If Not fedBatch AndAlso GraphicObject.InputConnectors(1).IsAttached Then
                Throw New InvalidOperationException("The second Julia inlet is for fed-batch supplementation. Disconnect it in Batch or Continuous mode; combine continuous feeds upstream.")
            End If

            Dim script = FlowSheet.Scripts.Values.FirstOrDefault(Function(s) s.Title = JuliaModelScriptName)
            If script Is Nothing OrElse script.PythonInterpreter <> Scripts.Interpreter.Julia Then
                Throw New InvalidOperationException("Select a Julia model script from this flowsheet's Script Manager.")
            End If
            If continuous AndAlso JuliaSteadyStateTolerance <> 0.00000001 AndAlso Not script.ScriptText.Contains("steady_tolerance") Then
                FlowSheet.ShowMessage(GraphicObject.Tag & ": the selected Julia script predates the Steady-state Tolerance property and keeps its built-in 1e-8 criterion. Replace its source with the current Model C script in Script Manager for the property to take effect.", IFlowsheet.MessageType.Warning)
            End If
            Dim names = {BiomassCompound, SubstrateCompound, XyloseCompound, ProductCompound, XylitolCompound}
            Dim roles = names.Concat({WaterCompound, CO2Compound}).ToArray()
            If roles.Any(Function(n) String.IsNullOrWhiteSpace(n)) OrElse roles.Distinct().Count() <> roles.Length Then
                Throw New InvalidOperationException("Select distinct biomass, glucose, xylose, ethanol, xylitol, water and CO2 compounds.")
            End If

            Dim feed = DirectCast(FlowSheet.SimulationObjects(
                GraphicObject.InputConnectors(0).AttachedConnector.AttachedFrom.Name), MaterialStream)
            Dim inletEnthalpyFlow = feed.Phases(0).Properties.massflow.GetValueOrDefault() *
                                    feed.Phases(0).Properties.enthalpy.GetValueOrDefault()
            Dim temperature = feed.Phases(0).Properties.temperature.GetValueOrDefault()
            If ThermalMode = BioReactorThermalMode.DefinedOutletTemperature Then temperature = OutletTemperature
            If ThermalMode = BioReactorThermalMode.Adiabatic OrElse Math.Abs(temperature - 303.15) > 0.01 Then
                Throw New InvalidOperationException("The paper's Julia model requires 30 C, controlled pH 5 and a fixed temperature. Select Isothermal with the inlet at 30 C, or Defined Outlet Temperature at 30 C; an adiabatic model is not provided.")
            End If
            Dim pressure = feed.Phases(0).Properties.pressure.GetValueOrDefault() - DeltaP.GetValueOrDefault()
            If pressure <= 0.0 Then Throw New InvalidOperationException("Outlet pressure must be positive.")

            Dim charge = PrepareFeedAtConditions(feed, temperature, pressure)
            Dim compounds = charge.Phases(0).Compounds
            If roles.Any(Function(n) Not compounds.ContainsKey(n)) Then
                Throw New InvalidOperationException("One of the selected Julia co-fermentation compounds is missing from the feed.")
            End If
            Dim flow = FeedVolumeFlow(charge)
            If Not continuous Then
                Dim expectedFlow = Volume / BatchDuration
                If Math.Abs(flow / expectedFlow - 1.0) > If(fedBatch, 0.000001, 0.01) Then
                    Throw New InvalidOperationException(String.Format(CultureInfo.InvariantCulture,
                        "The first inlet represents the initial charge averaged over a cycle. For {0:G8} m3 and {1:G8} h, set its flow to {2:G8} m3/h at 30 C (current {3:G8}). Click Apply Initial Charge Average to First Inlet after changing volume or duration; this preserves composition. For an upstream-controlled inlet, adjust its source.",
                        Volume, BatchDuration / 3600.0, expectedFlow * 3600.0, flow * 3600.0))
                End If
            End If
            Dim initial = names.Select(Function(n) compounds(n).MassFlow.GetValueOrDefault() / flow).ToArray()
            Dim feedConcentrations = If(continuous, DirectCast(initial.Clone(), Double()), New Double(4) {})
            Dim feedingFlow = If(continuous, flow, 0.0)
            Dim inletMass = compounds.ToDictionary(Function(kv) kv.Key, Function(kv) kv.Value.MassFlow.GetValueOrDefault())
            Dim scheduledCharge = If(fedBatch AndAlso UseFeedSchedule, PrepareScheduledFeed(flow, temperature, pressure), Nothing)
            If fedBatch AndAlso GraphicObject.InputConnectors(1).IsAttached Then
                Dim feedingStream = DirectCast(FlowSheet.SimulationObjects(
                    GraphicObject.InputConnectors(1).AttachedConnector.AttachedFrom.Name), MaterialStream)
                inletEnthalpyFlow += feedingStream.Phases(0).Properties.massflow.GetValueOrDefault() *
                                     feedingStream.Phases(0).Properties.enthalpy.GetValueOrDefault()
                Dim feedingCharge = If(scheduledCharge, PrepareFeedAtConditions(feedingStream, temperature, pressure))
                feedingFlow = FeedVolumeFlow(feedingCharge)
                Dim feedingCompounds = feedingCharge.Phases(0).Compounds
                If roles.Any(Function(n) Not feedingCompounds.ContainsKey(n)) Then
                    Throw New InvalidOperationException("A selected co-fermentation compound is missing from the supplemental feed.")
                End If
                feedConcentrations = names.Select(Function(n) feedingCompounds(n).MassFlow.GetValueOrDefault() / feedingFlow).ToArray()
                For Each compound In feedingCompounds
                    If Not inletMass.ContainsKey(compound.Key) Then inletMass(compound.Key) = 0.0
                    inletMass(compound.Key) += compound.Value.MassFlow.GetValueOrDefault()
                Next
            End If
            If continuous Then initial(0) = JuliaInitialBiomass_gL
            Dim duration = BatchDuration
            If continuous Then
                duration = If(JuliaMaxIntegrationTime_s > 0.0, JuliaMaxIntegrationTime_s, Math.Max(200.0 * 3600.0, 40.0 * Volume / flow))
            End If
            If Not Double.IsFinite(duration) Then Throw New InvalidOperationException("The inlet flow and volume must give a finite residence time.")
            Dim trajectory = RunJuliaModel(script.ScriptText, initial, feedConcentrations, feedingFlow, duration)
            Dim final = {trajectory.X.Last(), trajectory.S.Last(), trajectory.Xylose.Last(),
                         trajectory.P.Last(), trajectory.Xylitol.Last()}
            Dim outletVolumeFlow = If(fedBatch, trajectory.BrothVolume.Last() / BatchDuration, flow)

            ' The five independent empirical balances do not specify N/S assimilation,
            ' redox coproducts or CO2. Keep the five fitted states and the water feed intact,
            ' close total mass with an explicitly labelled CO2 surrogate on the gas port.
            Dim residualMass = names.Sum(Function(n) inletMass(n)) - final.Sum() * outletVolumeFlow
            ' Retain the exact arithmetic of saved batch cases.
            If OperatingMode = BioReactorMode.Batch Then residualMass = (initial.Sum() - final.Sum()) * flow
            If residualMass < -0.00000001 * Math.Max(1.0, inletMass.Values.Sum()) Then
                Throw New InvalidOperationException("The Julia model produces more tracked product mass than it consumes; cannot construct the material outlets.")
            End If
            Dim brothMass = New Dictionary(Of String, Double)(inletMass)
            For i = 0 To names.Length - 1
                brothMass(names(i)) = final(i) * outletVolumeFlow
            Next
            Dim gasMass = inletMass.Keys.ToDictionary(Function(n) n, Function(n) 0.0)
            gasMass(CO2Compound) = brothMass(CO2Compound) + Math.Max(0.0, residualMass)
            brothMass(CO2Compound) = 0.0

            Dim broth = DirectCast(FlowSheet.SimulationObjects(
                GraphicObject.OutputConnectors(0).AttachedConnector.AttachedTo.Name), MaterialStream)
            Dim gas = DirectCast(FlowSheet.SimulationObjects(
                GraphicObject.OutputConnectors(1).AttachedConnector.AttachedTo.Name), MaterialStream)
            WriteSplitStream(broth, brothMass, brothMass.Values.Sum(), temperature, pressure)
            WriteSplitStream(gas, gasMass, gasMass.Values.Sum(), temperature, pressure)
            broth.SetPropertyPackage(PropertyPackage)
            gas.SetPropertyPackage(PropertyPackage)
            PropertyPackage.CurrentMaterialStream = broth
            broth.Calculate(True, True)
            If gasMass.Values.Sum() > 0.0 Then
                PropertyPackage.CurrentMaterialStream = gas
                gas.Calculate(True, True)
            End If

            LastTrajectory = trajectory
            Result_X_gL = final(0)
            Result_S_gL = final(1)
            Result_Xylose_gL = final(2)
            Result_P_gL = final(3)
            Result_Xylitol_gL = final(4)
            If continuous Then
                Result_Mu_h = trajectory.Mu.Last()
            ElseIf fedBatch Then
                Dim integral = 0.0
                For i = 1 To trajectory.Times.Count - 1
                    integral += (trajectory.Mu(i - 1) + trajectory.Mu(i)) *
                                (trajectory.Times(i) - trajectory.Times(i - 1)) / 2.0
                Next
                Result_Mu_h = integral / BatchDuration
            Else
                Result_Mu_h = If(initial(0) > 0.0 AndAlso final(0) > 0.0,
                                 Math.Log(final(0) / initial(0)) / (BatchDuration / 3600.0), 0.0)
            End If
            Result_JuliaVolume_m3 = trajectory.BrothVolume.Last()
            Result_JuliaIntegrationTime_s = trajectory.Times.Last()
            Result_JuliaResidenceTime_s = If(continuous, Volume / flow, 0.0)
            Result_JuliaDilutionRate_h = If(continuous, flow / Volume * 3600.0, 0.0)
            Result_OUR_gLh = 0.0
            Result_CER_gLh = Double.NaN ' Not predicted; show the separate mass-closure result.
            Result_RQ = Double.NaN
            Result_JuliaClosureGas_kg_s = Math.Max(0.0, residualMass)
            Result_Q_metabolic_kW = Double.NaN ' Not predicted by this kinetic model.
            Result_OutletTemperature_K = temperature
            Result_Q_duty_kW = brothMass.Values.Sum() * broth.Phases(0).Properties.enthalpy.GetValueOrDefault() +
                              gasMass.Values.Sum() * gas.Phases(0).Properties.enthalpy.GetValueOrDefault() - inletEnthalpyFlow
            DeltaQ = Result_Q_duty_kW
            Dim energy = GetEnergyStream()
            If energy IsNot Nothing Then
                energy.EnergyFlow = -Result_Q_duty_kW
                energy.GraphicObject.Calculated = True
            End If
            OutletTemperature = temperature
            FlowSheet.ShowMessage(GraphicObject.Tag & ": Julia user script completed. CO2 is a total-mass closure surrogate; duty is a DWSIM enthalpy closure, not a paper heat prediction.", IFlowsheet.MessageType.Information)
        End Sub

        Private Function RunJuliaModel(modelSource As String, initial As Double(), feed As Double(),
                                       feedingFlow As Double, duration As Double) As BioReactorTrajectoryResult
            Dim directory = Environment.CurrentDirectory
            If Not String.IsNullOrWhiteSpace(FlowSheet.FilePath) Then directory = Path.GetDirectoryName(Path.GetFullPath(FlowSheet.FilePath))
            Dim outputPath = Path.Combine(Path.GetTempPath(), "dwsim-bioreactor-" & Guid.NewGuid().ToString("N") & ".csv")
            Dim hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(modelSource)))
            Dim ci = CultureInfo.InvariantCulture
            Dim source As New StringBuilder()
            source.AppendLine("using Base64")
            source.AppendLine("let")
            source.AppendLine("    if !isdefined(Main, :_dwsim_bioreactor_model_hash) || _dwsim_bioreactor_model_hash != """ & hash & """ || !isdefined(Main, :_dwsim_bioreactor_entrypoint)")
            source.AppendLine("        model = Module(gensym(:DWSIMBioKinetics))")
            source.AppendLine("        Base.include_string(model, String(base64decode(""" & Convert.ToBase64String(Encoding.UTF8.GetBytes(modelSource)) & """)), " & JuliaString(Path.Combine(directory, "bioreactor_model.jl")) & ")")
            source.AppendLine("        entrypoint = if isdefined(model, :run_bioreactor)")
            source.AppendLine("            Base.invokelatest(getfield, model, :run_bioreactor)")
            ' Compatibility with the first saved Model C scripts, before the public entry point.
            source.AppendLine("        elseif isdefined(model, :ZMobilis2019) && isdefined(Base.invokelatest(getfield, model, :ZMobilis2019), :run_bioreactor)")
            source.AppendLine("            kinetic_model = Base.invokelatest(getfield, model, :ZMobilis2019)")
            source.AppendLine("            Base.invokelatest(getfield, kinetic_model, :run_bioreactor)")
            source.AppendLine("        else")
            source.AppendLine("            error(""Define run_bioreactor(initial, duration_h, output_path) in the selected Julia script."")")
            source.AppendLine("        end")
            source.AppendLine("        entrypoint isa Function || error(""run_bioreactor must be a Julia function."")")
            source.AppendLine("        global _dwsim_bioreactor_entrypoint = entrypoint")
            source.AppendLine("        global _dwsim_bioreactor_model_hash = """ & hash & """")
            source.AppendLine("    end")
            Dim mode = If(OperatingMode = BioReactorMode.Continuous, "continuous", If(OperatingMode = BioReactorMode.FedBatch, "fed_batch", "batch"))
            Dim useSchedule = OperatingMode = BioReactorMode.FedBatch AndAlso UseFeedSchedule
            Dim pulses = If(useSchedule, FeedPulses, New List(Of BioReactorFeedPulse))
            Dim pulseType = "NamedTuple{(:start_h, :amount_m3, :rate_m3_h), Tuple{Float64, Float64, Float64}}"
            Dim pulseValues = String.Join(",", pulses.Select(
                Function(p) "(start_h=Float64(" & p.StartTime_h.ToString("R", ci) & "), amount_m3=Float64(" & p.Volume_m3.ToString("R", ci) &
                            "), rate_m3_h=Float64(" & p.Rate_m3_h.ToString("R", ci) & "))"))
            Dim pulseArray = pulseType & "[" & pulseValues & "]"
            source.AppendLine("    initial = Float64[" & String.Join(",", initial.Select(Function(v) v.ToString("R", ci))) & "]")
            source.AppendLine("    duration_h = Float64(" & (duration / 3600.0).ToString("R", ci) & ")")
            source.AppendLine("    output_path = " & JuliaString(outputPath))
            source.AppendLine("    operation = (mode=:" & mode & ", volume_m3=Float64(" & Volume.ToString("R", ci) & ")" &
                              ", duration_h=duration_h, feed_flow_m3_h=Float64(" & (feedingFlow * 3600.0).ToString("R", ci) & ")" &
                              ", feed_g_L=Float64[" & String.Join(",", feed.Select(Function(v) v.ToString("R", ci))) & "]" &
                              ", use_feed_schedule=" & If(useSchedule, "true", "false") &
                              ", feed_total_m3=Float64(" & If(useSchedule, pulses.Sum(Function(p) p.Volume_m3), feedingFlow * duration).ToString("R", ci) & ")" &
                              ", feed_pulses=" & pulseArray &
                              ", steady_tolerance=Float64(" & JuliaSteadyStateTolerance.ToString("R", ci) & "))")
            source.AppendLine("    if Base.invokelatest(applicable, _dwsim_bioreactor_entrypoint, initial, duration_h, output_path, operation)")
            source.AppendLine("        Base.invokelatest(_dwsim_bioreactor_entrypoint, initial, duration_h, output_path, operation)")
            source.AppendLine("    elseif operation.mode == :batch")
            source.AppendLine("        Base.invokelatest(_dwsim_bioreactor_entrypoint, initial, duration_h, output_path)")
            source.AppendLine("    else")
            source.AppendLine("        error(""The selected Julia script has only the legacy Batch interface. Add run_bioreactor(initial, duration_h, output_path, operation) for Fed-batch/Continuous, or select the updated Model C script."")")
            source.AppendLine("    end")
            source.AppendLine("end")
            Try
                JuliaScriptRunner.ForFlowsheet(FlowSheet).RunSource(source.ToString(), directory,
                    Sub(line) FlowSheet.ShowMessage(line, IFlowsheet.MessageType.Information),
                    Function() GlobalSettings.Settings.CalculatorStopRequested)
                Dim lines = File.ReadAllLines(outputPath)
                Const header As String = "time_h,biomass_g_L,glucose_g_L,xylose_g_L,ethanol_g_L,xylitol_g_L,mu_h,q_glucose_h,q_xylose_h,q_ethanol_h"
                Dim hasVolume = lines.Length > 0 AndAlso lines(0) = header & ",volume_m3"
                If lines.Length < 3 OrElse (lines(0) <> header AndAlso Not hasVolume) Then Throw New InvalidDataException("Julia returned an invalid co-fermentation trajectory.")
                If OperatingMode <> BioReactorMode.Batch AndAlso Not hasVolume Then
                    Throw New InvalidDataException("Fed-batch and Continuous scripts must append volume_m3 to the trajectory CSV.")
                End If
                Dim result As New BioReactorTrajectoryResult With {.Mode = "CoFermentation"}
                For Each line In lines.Skip(1)
                    Dim row = line.Split(","c).Select(Function(v) Double.Parse(v, NumberStyles.Float, ci)).ToArray()
                    If row.Length <> If(hasVolume, 11, 10) OrElse row.Any(Function(v) Not Double.IsFinite(v) OrElse v < 0.0) Then
                        Throw New InvalidDataException("Julia returned a non-finite, negative or incomplete trajectory row.")
                    End If
                    If result.Times.Count > 0 AndAlso row(0) * 3600.0 <= result.Times.Last() Then
                        Throw New InvalidDataException("Julia trajectory times must increase.")
                    End If
                    result.Times.Add(row(0) * 3600.0)
                    result.X.Add(row(1))
                    result.S.Add(row(2))
                    result.Glucose.Add(row(2))
                    result.Xylose.Add(row(3))
                    result.P.Add(row(4))
                    result.Xylitol.Add(row(5))
                    result.Mu.Add(row(6))
                    result.qS.Add(row(7) + row(8))
                    result.qP.Add(row(9))
                    Dim brothVolume = If(hasVolume, row(10), Volume)
                    If brothVolume <= 0.0 OrElse (result.BrothVolume.Count > 0 AndAlso brothVolume < result.BrothVolume.Last() - 0.0000001) Then
                        Throw New InvalidDataException("Julia culture volume must stay positive and may not decrease in the supported modes.")
                    End If
                    If OperatingMode <> BioReactorMode.FedBatch AndAlso Math.Abs(brothVolume - Volume) > 0.0000001 * Math.Max(1.0, Volume) Then
                        Throw New InvalidDataException("Batch and Continuous operation require constant working volume.")
                    End If
                    If OperatingMode = BioReactorMode.FedBatch AndAlso UseFeedSchedule Then
                        Dim scheduledVolume = Volume + GetScheduledFeedVolume(row(0) * 3600.0)
                        If Math.Abs(brothVolume - scheduledVolume) > 0.000001 * Math.Max(1.0, scheduledVolume) Then
                            Throw New InvalidDataException("The Julia trajectory does not follow the editor's feed pulses. Update the selected script's feed_rate_m3_h and feed_switch_times_h to use operation.use_feed_schedule and operation.feed_pulses.")
                        End If
                    End If
                    result.BrothVolume.Add(brothVolume)
                Next
                If result.Times(0) <> 0.0 OrElse (OperatingMode <> BioReactorMode.Continuous AndAlso Math.Abs(result.Times.Last() - duration) > 0.001) Then
                    Throw New InvalidDataException("Julia did not return the requested fermentation interval starting at zero.")
                End If
                Dim first = {result.X(0), result.S(0), result.Xylose(0), result.P(0), result.Xylitol(0)}
                For i = 0 To initial.Length - 1
                    If Math.Abs(first(i) - initial(i)) > 0.00000001 * Math.Max(1.0, initial(i)) Then
                        Throw New InvalidDataException("Julia did not use the supplied initial concentrations.")
                    End If
                Next
                If Math.Abs(result.BrothVolume(0) - Volume) > 0.0000001 * Math.Max(1.0, Volume) Then
                    Throw New InvalidDataException("Julia did not use the supplied initial volume.")
                End If
                If OperatingMode = BioReactorMode.FedBatch Then
                    Dim expectedVolume = Volume + feedingFlow * duration
                    If Math.Abs(result.BrothVolume.Last() - expectedVolume) > 0.000001 * Math.Max(1.0, expectedVolume) Then
                        Throw New InvalidDataException(String.Format(ci,
                            "The Julia feed curve and the supplemental stream specify different total feeds. The curve implies a cycle-average flow of {0:G8} m3/h; the stream supplies {1:G8} m3/h. Adjust the stream or feed_rate_m3_h so their integrated feed amounts agree.",
                            (result.BrothVolume.Last() - Volume) / duration * 3600.0, feedingFlow * 3600.0))
                    End If
                End If
                Return result
            Finally
                If File.Exists(outputPath) Then File.Delete(outputPath)
            End Try
        End Function

        Private Shared Function JuliaString(value As String) As String
            Return "String(base64decode(""" & Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) & """))"
        End Function

        Private Function GetJuliaReport(ci As CultureInfo, numberformat As String) As String
            Dim report As New StringBuilder()
            report.AppendLine("BioReactor: " & GraphicObject.Tag)
            report.AppendLine("User Script / Julia: glucose-xylose co-fermentation")
            report.AppendLine("Script: " & JuliaModelScriptName)
            report.AppendLine("Operating mode: " & OperatingMode.ToString() & "; 30 C, controlled pH 5.")
            report.AppendLine("Final working volume: " & Result_JuliaVolume_m3.ToString(numberformat, ci) & " m3")
            If OperatingMode = BioReactorMode.Continuous Then
                report.AppendLine("Steady-state outlet; hydraulic residence time: " & (Result_JuliaResidenceTime_s / 3600.0).ToString(numberformat, ci) & " h")
                report.AppendLine("Dilution rate: " & Result_JuliaDilutionRate_h.ToString(numberformat, ci) & " 1/h")
                report.AppendLine("Steady-state tolerance (scaled residual): " & JuliaSteadyStateTolerance.ToString("G3", ci) &
                                  "; integration horizon: " & If(JuliaMaxIntegrationTime_s > 0.0, (JuliaMaxIntegrationTime_s / 3600.0).ToString(numberformat, ci) & " h", "automatic, max(200 h, 40 x HRT)") &
                                  "; stopped at " & (Result_JuliaIntegrationTime_s / 3600.0).ToString(numberformat, ci) & " h.")
            Else
                report.AppendLine("Fermentation duration: " & (BatchDuration / 3600.0).ToString(numberformat, ci) & " h; streams represent cycle-average material flows.")
                If OperatingMode = BioReactorMode.FedBatch Then
                    If UseFeedSchedule Then
                        AppendFeedScheduleReport(report, ci)
                    Else
                        report.AppendLine("Variable broth volume; supplemental feed rate is defined by the Julia script.")
                    End If
                End If
            End If
            Dim labels = {"Biomass", "Glucose", "Xylose", "Ethanol", "Xylitol"}
            Dim values = {Result_X_gL, Result_S_gL, Result_Xylose_gL, Result_P_gL, Result_Xylitol_gL}
            For i = 0 To labels.Length - 1
                report.AppendLine(labels(i) & ": " & values(i).ToString(numberformat, ci) & " g/L")
            Next
            report.AppendLine(If(OperatingMode = BioReactorMode.Continuous, "Steady-state growth rate: ", "Average growth rate: ") & Result_Mu_h.ToString(numberformat, ci) & " 1/h")
            report.AppendLine("Untracked product mass as CO2 surrogate: " & Result_JuliaClosureGas_kg_s.ToString(numberformat, ci) & " kg/s")
            report.AppendLine("DWSIM enthalpy closure duty: " & Result_Q_duty_kW.ToString(numberformat, ci) & " kW")
            report.AppendLine("The paper does not supply CO2, nutrient stoichiometry or reaction heat equations. Gas/duty are interface closures, not validated process predictions.")
            Return report.ToString()
        End Function

    End Class
End Namespace
