Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.RegularExpressions
Imports DWSIM.Interfaces
Imports DWSIM.Interfaces.Enums
Imports DWSIM.SharedClasses.Scripting
Imports DWSIM.Thermodynamics.Streams
Imports Newtonsoft.Json.Linq

Namespace Reactors

    <Serializable> Public Class BioReactorModelValue
        Public Property ID As String
        Public Property Name As String
        Public Property Unit As String
        Public Property Value As Double
    End Class

    Public Partial Class Reactor_BioReactor

        Public Property JuliaModelScriptName As String = ""
        Public Property JuliaSteadyStateTolerance As Double = 0.00000001
        Public Property JuliaMaxIntegrationTime_s As Double = 0.0

        Private Shared Function JuliaString(value As String) As String
            Return "String(base64decode(""" & Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) & """))"
        End Function

        Public Property JuliaParametersJSON As String = "{}"
        ' Persist arbitrary series with their units; LastTrajectory itself is not serialized.
        Public Property JuliaGenericResultJSON As String = ""
        <NonSerialized> Private _genericJSON As String
        <NonSerialized> Private _genericResult As JObject
        <NonSerialized> Private _genericTrajectoryJSON As String

        Private Function GenericResult() As JObject
            If String.IsNullOrWhiteSpace(JuliaGenericResultJSON) Then Return Nothing
            If _genericResult Is Nothing OrElse _genericJSON <> JuliaGenericResultJSON Then
                _genericResult = JObject.Parse(JuliaGenericResultJSON)
                _genericJSON = JuliaGenericResultJSON
            End If
            Return _genericResult
        End Function

        <System.Xml.Serialization.XmlIgnore, Newtonsoft.Json.JsonIgnore>
        Public ReadOnly Property GenericModelName As String
            Get
                Dim r = GenericResult()
                Return If(r Is Nothing, "", CStr(r("model_name")))
            End Get
        End Property

        <System.Xml.Serialization.XmlIgnore, Newtonsoft.Json.JsonIgnore>
        Public ReadOnly Property GenericModelNotes As String
            Get
                Dim r = GenericResult()
                Return If(r Is Nothing, "", CStr(r("notes")))
            End Get
        End Property

        <System.Xml.Serialization.XmlIgnore, Newtonsoft.Json.JsonIgnore>
        Public ReadOnly Property GenericModelValues As List(Of BioReactorModelValue)
            Get
                Dim values As New List(Of BioReactorModelValue)
                Dim r = GenericResult()
                If r Is Nothing OrElse r("scalars") Is Nothing Then Return values
                For Each item In DirectCast(r("scalars"), JArray)
                    values.Add(New BioReactorModelValue With {.ID = CStr(item("id")), .Name = CStr(item("name")),
                        .Unit = CStr(item("unit")), .Value = CDbl(item("value"))})
                Next
                Return values
            End Get
        End Property

        Public Sub RestoreGenericTrajectory()
            If _genericTrajectoryJSON = JuliaGenericResultJSON AndAlso LastTrajectory IsNot Nothing Then Return
            Dim r = GenericResult()
            If r Is Nothing Then Return
            Dim trajectory As New BioReactorTrajectoryResult With {.Mode = "GenericJulia"}
            trajectory.Times = DirectCast(r("time_s"), JArray).Select(Function(v) CDbl(v)).ToList()
            For Each s In DirectCast(r("series"), JArray)
                trajectory.NamedSeries.Add(New BioReactorNamedSeries With {
                    .ID = CStr(s("id")), .Name = CStr(s("name")), .Unit = CStr(s("unit")),
                    .Group = If(CStr(s("group")), "Model states"),
                    .Values = DirectCast(s("values"), JArray).Select(Function(v) CDbl(v)).ToList()})
            Next
            LastTrajectory = trajectory
            _genericTrajectoryJSON = JuliaGenericResultJSON
        End Sub

        Private Shared Function GenericNumber(token As JToken, label As String) As Double
            If token Is Nothing OrElse (token.Type <> JTokenType.Integer AndAlso token.Type <> JTokenType.Float) Then
                Throw New InvalidDataException(label & " must be a number.")
            End If
            Dim value = CDbl(token)
            If Not Double.IsFinite(value) Then Throw New InvalidDataException(label & " must be finite.")
            Return value
        End Function

        Private Shared Function GenericText(token As JToken, label As String, Optional allowEmpty As Boolean = False) As String
            If token Is Nothing OrElse token.Type <> JTokenType.String OrElse
               (Not allowEmpty AndAlso String.IsNullOrWhiteSpace(CStr(token))) Then
                Throw New InvalidDataException(label & " must be a string" & If(allowEmpty, ".", " with a value."))
            End If
            Return CStr(token)
        End Function

        Private Shared Function JuliaLiteral(value As JToken) As String
            Select Case value.Type
                Case JTokenType.Object
                    Return "Dict{String,Any}(" & String.Join(",", DirectCast(value, JObject).Properties().Select(
                        Function(p) JuliaString(p.Name) & "=>" & JuliaLiteral(p.Value))) & ")"
                Case JTokenType.Array
                    Return "Any[" & String.Join(",", DirectCast(value, JArray).Select(Function(v) JuliaLiteral(v))) & "]"
                Case JTokenType.String : Return JuliaString(CStr(value))
                Case JTokenType.Boolean : Return If(CBool(value), "true", "false")
                Case JTokenType.Null : Return "nothing"
                Case JTokenType.Integer, JTokenType.Float
                    Return "Float64(" & GenericNumber(value, "Model setting").ToString("R", CultureInfo.InvariantCulture) & ")"
                Case Else : Throw New InvalidDataException("Unsupported JSON value in model settings.")
            End Select
        End Function

        Private Function GenericStreamContext(stream As MaterialStream, port As Integer) As JObject
            Dim flows As New JObject()
            Dim total = stream.Phases(0).Properties.massflow.GetValueOrDefault()
            If Not Double.IsFinite(total) OrElse total < 0 Then Throw New InvalidOperationException("Inlet mass flow must be finite and nonnegative.")
            For Each c In stream.Phases(0).Compounds.Values
                Dim mass = c.MassFraction.GetValueOrDefault() * total
                If Not Double.IsFinite(mass) OrElse mass < 0 Then Throw New InvalidOperationException("Inlet component flow must be finite and nonnegative: " & c.Name)
                flows.Add(c.Name, mass)
            Next
            Return New JObject From {
                {"port", port}, {"temperature_K", stream.Phases(0).Properties.temperature.GetValueOrDefault()},
                {"pressure_Pa", stream.Phases(0).Properties.pressure.GetValueOrDefault()},
                {"mass_flows_kg_s", flows}, {"mass_flow_kg_s", total},
                {"volume_flow_m3_s", stream.Phases(0).Properties.volumetric_flow.GetValueOrDefault()}}
        End Function

        Private Sub CalculateGenericJulia()
            If Not Double.IsFinite(Volume) OrElse Volume <= 0 OrElse Not Double.IsFinite(BatchDuration) OrElse BatchDuration <= 0 Then
                Throw New InvalidOperationException("The model requires a positive working volume and integration duration.")
            End If
            If Not [Enum].IsDefined(OperatingMode) OrElse Not [Enum].IsDefined(ThermalMode) Then Throw New InvalidOperationException("Select a supported operating and thermal mode.")
            If OperatingMode = BioReactorMode.FedBatch AndAlso UseFeedSchedule Then ValidateFeedSchedule()
            If OperatingMode = BioReactorMode.Continuous AndAlso (Not Double.IsFinite(JuliaSteadyStateTolerance) OrElse JuliaSteadyStateTolerance <= 0 OrElse Not Double.IsFinite(JuliaMaxIntegrationTime_s) OrElse JuliaMaxIntegrationTime_s < 0) Then
                Throw New InvalidOperationException("Steady-state tolerance must be positive and maximum integration time must be nonnegative.")
            End If
            Dim candidates = FlowSheet.Scripts.Values.Where(Function(s) s.Title = JuliaModelScriptName AndAlso s.PythonInterpreter = Scripts.Interpreter.Julia).ToList()
            If candidates.Count <> 1 Then Throw New InvalidOperationException("Select one Julia model with a unique title in Script Manager.")
            Dim settings As JObject
            Try
                settings = JObject.Parse(JuliaParametersJSON)
            Catch ex As Exception
                Throw New InvalidOperationException("Model settings must be a JSON object.", ex)
            End Try
            Dim inlets As New JArray()
            Dim compounds As New JObject()
            For Each cp In FlowSheet.SelectedCompounds.Values
                compounds.Add(cp.Name, New JObject From {{"molar_mass_kg_kmol", cp.Molar_Weight}})
            Next
            For i = 0 To GraphicObject.InputConnectors.Count - 1
                Dim connector = GraphicObject.InputConnectors(i)
                If connector.IsAttached Then
                    Dim stream = DirectCast(FlowSheet.SimulationObjects(connector.AttachedConnector.AttachedFrom.Name), MaterialStream)
                    inlets.Add(GenericStreamContext(stream, i))
                End If
            Next
            If inlets.Count = 0 Then Throw New InvalidOperationException("Connect at least one inlet to the BioReactor.")
            Dim pulses As New JArray()
            For Each pulse In FeedPulses
                pulses.Add(New JObject From {{"start_s", pulse.StartTime_h * 3600.0}, {"volume_m3", pulse.Volume_m3}, {"rate_m3_s", pulse.Rate_m3_h / 3600.0}})
            Next
            Dim outlets As New JArray()
            For i = 0 To GraphicObject.OutputConnectors.Count - 1
                If GraphicObject.OutputConnectors(i).IsAttached Then outlets.Add(i)
            Next
            If outlets.Count = 0 Then Throw New InvalidOperationException("Connect a material outlet to the BioReactor.")
            Dim operation As New JObject From {
                {"mode", OperatingMode.ToString()}, {"volume_m3", Volume}, {"duration_s", BatchDuration},
                {"thermal_mode", ThermalMode.ToString()}, {"outlet_temperature_K", OutletTemperature},
                {"pressure_drop_Pa", DeltaP.GetValueOrDefault()}, {"aerobic", IsAerobic},
                {"use_feed_schedule", UseFeedSchedule}, {"feed_pulses", pulses},
                {"steady_tolerance", JuliaSteadyStateTolerance}, {"max_integration_time_s", JuliaMaxIntegrationTime_s}}
            Dim context As New JObject From {{"schema_version", 1}, {"operation", operation}, {"parameters", settings},
                {"inlets", inlets}, {"compounds", compounds}, {"connected_outlet_ports", outlets}}
            Dim directory = Environment.CurrentDirectory
            If Not String.IsNullOrWhiteSpace(FlowSheet.FilePath) Then directory = Path.GetDirectoryName(Path.GetFullPath(FlowSheet.FilePath))
            Dim outputPath = Path.Combine(Path.GetTempPath(), "dwsim-generic-bio-" & Guid.NewGuid().ToString("N") & ".json")
            Dim script = candidates(0)
            Dim hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script.ScriptText & directory)))
            Dim bridge As String
            Using reader As New StreamReader(GetType(Reactor_BioReactor).Assembly.GetManifestResourceStream("DWSIM.UnitOperations.BioReactor.GenericBridge.jl"))
                bridge = reader.ReadToEnd()
            End Using
            bridge = bridge.Replace("@DWSIM_MODEL_HASH@", JuliaString(hash)).Replace("@DWSIM_MODEL_SOURCE@", JuliaString(script.ScriptText)).
                Replace("@DWSIM_SOURCE_FILENAME@", JuliaString(Path.Combine(directory, "bioreactor_model.jl"))).
                Replace("@DWSIM_CONTEXT@", JuliaLiteral(context)).Replace("@DWSIM_OUTPUT_PATH@", JuliaString(outputPath))
            Dim result As JObject
            Try
                JuliaScriptRunner.ForFlowsheet(FlowSheet).RunSource(bridge, directory,
                    Sub(line) FlowSheet.ShowMessage(line, IFlowsheet.MessageType.Information), Function() GlobalSettings.Settings.CalculatorStopRequested)
                result = JObject.Parse(File.ReadAllText(outputPath))
            Finally
                If File.Exists(outputPath) Then File.Delete(outputPath)
            End Try
            ValidateGenericTrajectory(result)
            If OperatingMode <> BioReactorMode.Continuous AndAlso Math.Abs(CDbl(result("time_s").Last()) - BatchDuration) > 0.001 Then
                Throw New InvalidDataException("Batch and fed-batch models must return the requested integration duration.")
            End If
            Dim pending As New List(Of Tuple(Of MaterialStream, Dictionary(Of String, Double), Double, Double))
            Dim seen As New HashSet(Of Integer)
            Dim massOut As Double = 0
            If Not TypeOf result("outlets") Is JArray Then Throw New InvalidDataException("Model result must include an outlets array.")
            For Each item In DirectCast(result("outlets"), JArray)
                Dim p = GenericNumber(item("port"), "Outlet port")
                If p <> Math.Truncate(p) OrElse p < 0 OrElse p >= GraphicObject.OutputConnectors.Count Then Throw New InvalidDataException("Invalid outlet port.")
                Dim port = CInt(p)
                If Not seen.Add(port) Then Throw New InvalidDataException("Duplicate outlet port.")
                If Not GraphicObject.OutputConnectors(port).IsAttached Then Throw New InvalidDataException("The model returned an unconnected outlet port: " & port)
                Dim t = GenericNumber(item("temperature_K"), "Outlet temperature")
                Dim pressure = GenericNumber(item("pressure_Pa"), "Outlet pressure")
                If t <= 0 OrElse pressure <= 0 Then Throw New InvalidDataException("Outlet temperature and pressure must be positive.")
                If Not TypeOf item("mass_flows_kg_s") Is JObject Then Throw New InvalidDataException("Outlet mass_flows_kg_s must be an object keyed by compound name.")
                Dim masses As New Dictionary(Of String, Double)
                For Each prop In DirectCast(item("mass_flows_kg_s"), JObject).Properties()
                    If Not FlowSheet.SelectedCompounds.ContainsKey(prop.Name) Then Throw New InvalidDataException("Model outlet contains an unselected compound: " & prop.Name)
                    Dim value = GenericNumber(prop.Value, prop.Name & " mass flow")
                    If value < 0 Then Throw New InvalidDataException("Negative outlet component flow: " & prop.Name)
                    masses.Add(prop.Name, value)
                Next
                massOut += masses.Values.Sum()
                Dim stream = DirectCast(FlowSheet.SimulationObjects(GraphicObject.OutputConnectors(port).AttachedConnector.AttachedTo.Name), MaterialStream)
                pending.Add(Tuple.Create(stream, masses, t, pressure))
            Next
            If seen.Count <> outlets.Count Then Throw New InvalidDataException("Return every connected material outlet, including zero-flow outlets.")
            Dim massIn = inlets.Sum(Function(i) CDbl(i("mass_flow_kg_s")))
            Dim accumulation = If(result("mass_accumulation_kg_s") Is Nothing, 0.0, GenericNumber(result("mass_accumulation_kg_s"), "Mass accumulation"))
            Dim residual = massIn - massOut - accumulation
            If Math.Abs(residual) > 0.00000001 * Math.Max(massIn, massOut) + 0.000000000001 Then
                Throw New InvalidDataException("The Julia model does not close total mass: residual " & residual.ToString("G8", CultureInfo.InvariantCulture) & " kg/s. Supply physical outlet flows and any explicit inventory accumulation; the interface does not invent a gas closure.")
            End If
            Dim duty As Double? = Nothing
            If result("heat_duty_W") IsNot Nothing AndAlso result("heat_duty_W").Type <> JTokenType.Null Then duty = GenericNumber(result("heat_duty_W"), "Heat duty") / 1000.0
            Dim energy = GetEnergyStream()
            If energy IsNot Nothing AndAlso Not duty.HasValue Then Throw New InvalidDataException("A connected energy stream requires the model to return heat_duty_W (positive into the reactor).")
            ' Commit only after the whole response has been checked.
            For Each item In pending
                WriteSplitStream(item.Item1, item.Item2, item.Item2.Values.Sum(), item.Item3, item.Item4)
                item.Item1.SetPropertyPackage(PropertyPackage)
            Next
            Result_Q_duty_kW = If(duty.HasValue, duty.Value, Double.NaN)
            Result_Q_metabolic_kW = Double.NaN
            DeltaQ = duty
            If energy IsNot Nothing Then
                energy.EnergyFlow = -duty.Value
                energy.GraphicObject.Calculated = True
            End If
            Result_OutletTemperature_K = pending(0).Item3
            JuliaGenericResultJSON = result.ToString(Newtonsoft.Json.Formatting.None)
            RestoreGenericTrajectory()
            FlowSheet.ShowMessage(GraphicObject.Tag & ": " & GenericModelName & " completed (generic Julia interface).", IFlowsheet.MessageType.Information)
        End Sub

        Private Shared Sub ValidateGenericTrajectory(result As JObject)
            If GenericNumber(result("schema_version"), "Result schema version") <> 1 Then Throw New InvalidDataException("Unsupported BioReactor result schema version.")
            GenericText(result("model_name"), "Model name")
            If result("notes") IsNot Nothing Then GenericText(result("notes"), "Model notes", True)
            If Not TypeOf result("time_s") Is JArray OrElse Not TypeOf result("series") Is JArray Then Throw New InvalidDataException("Return time_s and series arrays.")
            Dim times = DirectCast(result("time_s"), JArray).Select(Function(v) GenericNumber(v, "Trajectory time")).ToArray()
            If times.Length < 2 OrElse times(0) <> 0 Then Throw New InvalidDataException("Return at least two trajectory times, starting at zero.")
            For i = 1 To times.Length - 1
                If times(i) <= times(i - 1) Then Throw New InvalidDataException("Trajectory times must strictly increase.")
            Next
            Dim ids As New HashSet(Of String)
            Dim groups As New Dictionary(Of String, String)
            For Each series In DirectCast(result("series"), JArray)
                Dim id = GenericText(series("id"), "Series id")
                If Not Regex.IsMatch(id, "^[A-Za-z][A-Za-z0-9_]*$") OrElse Not ids.Add(id) Then Throw New InvalidDataException("Series ids must be unique letters, digits and underscores, starting with a letter.")
                GenericText(series("name"), "Series name")
                Dim unit = GenericText(series("unit"), "Series unit", True)
                Dim group = If(series("group") Is Nothing, "Model states", GenericText(series("group"), "Chart group"))
                If groups.ContainsKey(group) AndAlso groups(group) <> unit Then Throw New InvalidDataException("Series in one chart group must use the same unit. Put different units in separate groups.")
                groups(group) = unit
                If Not TypeOf series("values") Is JArray OrElse series("values").Count() <> times.Length Then Throw New InvalidDataException("Every series must have one value per trajectory time.")
                For Each value In DirectCast(series("values"), JArray) : GenericNumber(value, id) : Next
            Next
            If ids.Count = 0 Then Throw New InvalidDataException("Return at least one state or rate series.")
            If result("scalars") IsNot Nothing Then
                If Not TypeOf result("scalars") Is JArray Then Throw New InvalidDataException("Scalars must be an array.")
                For Each item In DirectCast(result("scalars"), JArray)
                    Dim id = GenericText(item("id"), "Result id")
                    If Not Regex.IsMatch(id, "^[A-Za-z][A-Za-z0-9_]*$") OrElse Not ids.Add(id) Then Throw New InvalidDataException("Result ids must be unique letters, digits and underscores.")
                    GenericText(item("name"), "Result name") : GenericText(item("unit"), "Result unit", True)
                    GenericNumber(item("value"), id)
                Next
            End If
        End Sub

        Private Function GenericProperties(kind As PropertyType) As String()
            Dim inputs = {"Kinetic Model", "Operating Mode", "Working Volume", "Batch Duration", "Thermal Mode", "Outlet Temperature Setpoint",
                          "Aerobic", "Julia Model Script Name", "Julia Model Settings", "Steady-state Tolerance", "Max Integration Time", "Use Feed Schedule", "Feed Schedule"}
            If kind = PropertyType.WR Then Return inputs
            RestoreGenericTrajectory()
            Dim outputs As New List(Of String)
            If LastTrajectory IsNot Nothing Then outputs.AddRange(LastTrajectory.NamedSeries.Select(Function(s) "Julia Result: " & s.ID))
            outputs.AddRange(GenericModelValues.Select(Function(v) "Julia Result: " & v.ID))
            If kind = PropertyType.RO Then Return outputs.ToArray()
            Return inputs.Concat(outputs).ToArray()
        End Function

        Private Function GenericPropertyValue(prop As String, unitOnly As Boolean) As Object
            RestoreGenericTrajectory()
            Dim id = prop.Substring("Julia Result: ".Length)
            If LastTrajectory IsNot Nothing Then
                Dim state = LastTrajectory.NamedSeries.FirstOrDefault(Function(s) s.ID = id)
                If state IsNot Nothing Then Return If(unitOnly, CType(state.Unit, Object), CType(state.Values.Last(), Object))
            End If
            Dim result = GenericModelValues.FirstOrDefault(Function(v) v.ID = id)
            If result IsNot Nothing Then Return If(unitOnly, CType(result.Unit, Object), CType(result.Value, Object))
            Throw New ArgumentException("Unknown Julia result: " & id)
        End Function

        Private Function GenericChartNames() As List(Of String)
            RestoreGenericTrajectory()
            If LastTrajectory Is Nothing Then Return New List(Of String)
            Return LastTrajectory.NamedSeries.Select(Function(s) s.Group).Distinct().ToList()
        End Function

        Private Function GenericChart(name As String) As Object
            RestoreGenericTrajectory()
            If LastTrajectory Is Nothing Then Return Nothing
            Dim series = LastTrajectory.NamedSeries.Where(Function(s) s.Group = name).ToList()
            If series.Count = 0 Then Return Nothing
            Dim plot As New OxyPlot.PlotModel With {.Title = GraphicObject.Tag, .Subtitle = name, .LegendPlacement = OxyPlot.LegendPlacement.Outside}
            plot.Axes.Add(New OxyPlot.Axes.LinearAxis With {.Position = OxyPlot.Axes.AxisPosition.Bottom, .Title = "Time (h)"})
            plot.Axes.Add(New OxyPlot.Axes.LinearAxis With {.Position = OxyPlot.Axes.AxisPosition.Left, .Title = series(0).Unit})
            For Each s In series
                Dim line As New OxyPlot.Series.LineSeries With {.Title = s.Name}
                For i = 0 To LastTrajectory.Times.Count - 1
                    line.Points.Add(New OxyPlot.DataPoint(LastTrajectory.Times(i) / 3600.0, s.Values(i)))
                Next
                plot.Series.Add(line)
            Next
            Return plot
        End Function

        Private Function GenericReport(ci As CultureInfo, numberformat As String) As String
            RestoreGenericTrajectory()
            Dim report As New StringBuilder()
            report.AppendLine("BioReactor: " & GraphicObject.Tag & " / Julia model: " & GenericModelName)
            report.AppendLine("Script: " & JuliaModelScriptName)
            If LastTrajectory IsNot Nothing Then
                report.AppendLine("Final time: " & (LastTrajectory.Times.Last() / 3600.0).ToString(numberformat, ci) & " h")
                For Each s In LastTrajectory.NamedSeries
                    report.AppendLine(s.Name & ": " & s.Values.Last().ToString(numberformat, ci) & " " & s.Unit)
                Next
            End If
            For Each value In GenericModelValues
                report.AppendLine(value.Name & ": " & value.Value.ToString(numberformat, ci) & " " & value.Unit)
            Next
            report.AppendLine(GenericModelNotes)
            Return report.ToString()
        End Function

    End Class
End Namespace
