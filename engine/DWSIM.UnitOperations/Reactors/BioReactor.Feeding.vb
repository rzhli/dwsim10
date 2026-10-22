Imports System.Collections.Generic
Imports System.Globalization
Imports System.Linq
Imports DWSIM.Interfaces.Enums
Imports DWSIM.Thermodynamics.Streams

Namespace Reactors

    <Serializable>
    Public Class BioReactorFeedPulse
        Public Property StartTime_h As Double
        Public Property Volume_m3 As Double
        Public Property Rate_m3_h As Double
    End Class

    Public Partial Class Reactor_BioReactor

        Public Property UseFeedSchedule As Boolean = False
        <System.Xml.Serialization.XmlIgnore>
        Public Property FeedPulses As New List(Of BioReactorFeedPulse)
        Public Property Result_WorkingVolume_m3 As Double

        ''' <summary>Set the initial-charge inlet or its independent mixer sources to the cycle-average flow required by Volume / BatchDuration, preserving composition.</summary>
        Public Sub ApplyInitialChargeAverageToInlet()
            If OperatingMode = BioReactorMode.Continuous Then
                Throw New InvalidOperationException("The initial-charge average applies to batch and fed-batch operation, not continuous operation.")
            End If
            If Not Double.IsFinite(Volume) OrElse Volume <= 0.0 OrElse Not Double.IsFinite(BatchDuration) OrElse BatchDuration <= 0.0 Then
                Throw New InvalidOperationException("Set a positive working volume and batch duration first.")
            End If
            If Not GraphicObject.InputConnectors(0).IsAttached Then
                Throw New InvalidOperationException("Connect the initial-charge stream first.")
            End If

            Dim feed = DirectCast(FlowSheet.SimulationObjects(
                GraphicObject.InputConnectors(0).AttachedConnector.AttachedFrom.Name), MaterialStream)
            Dim sources = GetInitialChargeSourceStreams(feed)

            Dim temperature = feed.Phases(0).Properties.temperature.GetValueOrDefault()
            If (KineticModel = BioKineticModel.JuliaCoFermentation AndAlso ThermalMode = BioReactorThermalMode.DefinedOutletTemperature) OrElse
               (KineticModel <> BioKineticModel.JuliaCoFermentation AndAlso ReactorOperationMode = OperationMode.OutletTemperature) Then temperature = OutletTemperature
            Dim pressure = feed.Phases(0).Properties.pressure.GetValueOrDefault() - DeltaP.GetValueOrDefault()
            If pressure <= 0.0 Then Throw New InvalidOperationException("Set a positive initial-charge pressure first.")

            Dim charge = PrepareFeedAtConditions(feed, temperature, pressure)
            Dim flow = FeedVolumeFlow(charge)
            Dim scale = (Volume / BatchDuration) / flow
            Dim sourceMassFlow = sources.Sum(Function(source) source.Phases(0).Properties.massflow.GetValueOrDefault())
            Dim feedMassFlow = charge.Phases(0).Properties.massflow.GetValueOrDefault()
            If Not Double.IsFinite(scale) OrElse scale <= 0.0 OrElse
               Not Double.IsFinite(sourceMassFlow) OrElse sourceMassFlow <= 0.0 OrElse
               Not Double.IsFinite(feedMassFlow) OrElse feedMassFlow <= 0.0 Then
                Throw New InvalidOperationException("Calculate the initial-charge stream with a positive flow and valid composition first.")
            End If
            If Math.Abs(sourceMassFlow / feedMassFlow - 1.0) > 0.00001 Then
                Throw New InvalidOperationException("The initial-charge mixer or its source streams are out of date. Recalculate the flowsheet, then apply the initial-charge average again.")
            End If

            For Each source In sources
                Dim currentMassFlow = source.Phases(0).Properties.massflow.GetValueOrDefault()
                If currentMassFlow <= 0.0 Then Continue For
                FlowSheet.RegisterSnapshot(SnapshotType.ObjectData, source)
                source.SetMassFlow(currentMassFlow * scale)
                source.SetDirtyStatus(True)
                source.Calculated = False
                source.GraphicObject.Calculated = False
            Next

            Dim inletConnection = feed.GraphicObject.InputConnectors(0)
            If inletConnection.IsAttached Then
                Dim mixerGraphic = inletConnection.AttachedConnector?.AttachedFrom
                Dim mixer = If(mixerGraphic IsNot Nothing AndAlso FlowSheet.SimulationObjects.ContainsKey(mixerGraphic.Name),
                               TryCast(FlowSheet.SimulationObjects(mixerGraphic.Name), DWSIM.UnitOperations.UnitOperations.Mixer), Nothing)
                If mixer IsNot Nothing Then
                    mixer.SetDirtyStatus(True)
                    mixer.Calculated = False
                    mixer.GraphicObject.Calculated = False
                End If
            End If
            feed.SetDirtyStatus(True)
            feed.Calculated = False
            feed.GraphicObject.Calculated = False
            SetDirtyStatus(True)
            Calculated = False
            GraphicObject.Calculated = False
        End Sub

        Private Function GetInitialChargeSourceStreams(feed As MaterialStream) As List(Of MaterialStream)
            Dim inlet = feed.GraphicObject.InputConnectors(0)
            If Not inlet.IsAttached Then
                EnsureIndependentFeedSource(feed)
                Return New List(Of MaterialStream) From {feed}
            End If

            Dim upstreamGraphic = inlet.AttachedConnector?.AttachedFrom
            Dim upstream = If(upstreamGraphic IsNot Nothing AndAlso FlowSheet.SimulationObjects.ContainsKey(upstreamGraphic.Name),
                              FlowSheet.SimulationObjects(upstreamGraphic.Name), Nothing)
            Dim mixer = TryCast(upstream, DWSIM.UnitOperations.UnitOperations.Mixer)
            If mixer Is Nothing Then
                Dim upstreamTag = If(upstreamGraphic Is Nothing, "upstream equipment", upstreamGraphic.Tag)
                    Throw New InvalidOperationException(String.Format(CultureInfo.InvariantCulture,
                    "The initial-charge stream is driven by {0}. Apply can proportionally adjust independent material streams connected directly to a Mixer; adjust this upstream source manually.", upstreamTag))
            End If
            If mixer.IsAdjustAttached OrElse mixer.IsSpecAttached Then
                Throw New InvalidOperationException("The initial-charge mixer is controlled by an Adjust or Spec. Remove that control or adjust its source streams manually.")
            End If
            If feed.IsAdjustAttached OrElse feed.IsSpecAttached Then
                Throw New InvalidOperationException("The initial-charge mixer outlet is controlled by an Adjust or Spec. Remove that control or adjust its source streams manually.")
            End If

            Dim sources As New List(Of MaterialStream)
            For Each connector In mixer.GraphicObject.InputConnectors
                If Not connector.IsAttached Then Continue For
                Dim sourceGraphic = connector.AttachedConnector?.AttachedFrom
                If sourceGraphic Is Nothing OrElse Not FlowSheet.SimulationObjects.ContainsKey(sourceGraphic.Name) OrElse
                   Not TypeOf FlowSheet.SimulationObjects(sourceGraphic.Name) Is MaterialStream Then
                    Throw New InvalidOperationException("Every connected initial-charge mixer inlet must come from a material stream.")
                End If
                Dim source = DirectCast(FlowSheet.SimulationObjects(sourceGraphic.Name), MaterialStream)
                EnsureIndependentFeedSource(source)
                sources.Add(source)
            Next
            If sources.Count = 0 Then
                Throw New InvalidOperationException("Connect at least one independently specified material stream to the initial-charge mixer.")
            End If
            Return sources
        End Function

        Private Shared Sub EnsureIndependentFeedSource(feed As MaterialStream)
            If feed.GraphicObject.InputConnectors.Any(Function(c) c.IsAttached) OrElse feed.IsAdjustAttached OrElse feed.IsSpecAttached Then
                Throw New InvalidOperationException(String.Format(CultureInfo.InvariantCulture,
                    "Initial-charge source stream '{0}' is controlled by upstream equipment, an Adjust, or a Spec. Apply requires independent source streams so it can scale them proportionally.", feed.GraphicObject.Tag))
            End If
            Dim massFlow = feed.Phases(0).Properties.massflow.GetValueOrDefault()
            If Not Double.IsFinite(massFlow) OrElse massFlow < 0.0 Then
                Throw New InvalidOperationException(String.Format(CultureInfo.InvariantCulture,
                    "Initial-charge source stream '{0}' must have a finite, non-negative mass flow before applying the cycle average.", feed.GraphicObject.Tag))
            End If
        End Sub

        ''' <summary>Validate non-overlapping constant-rate pulses and return their total volume (m3).</summary>
        Public Function ValidateFeedSchedule() As Double
            If Not Double.IsFinite(BatchDuration) OrElse BatchDuration <= 0.0 Then
                Throw New InvalidOperationException("Set a positive fed-batch duration before specifying feed pulses.")
            End If
            If FeedPulses Is Nothing OrElse FeedPulses.Count = 0 Then
                Throw New InvalidOperationException("Add at least one feed pulse, with a start time, positive volume and positive rate.")
            End If
            Dim previousEnd = 0.0
            Dim total = 0.0
            Dim duration_h = BatchDuration / 3600.0
            Dim timeTolerance = 0.0000000001 * Math.Max(1.0, duration_h)
            For Each pulse In FeedPulses.OrderBy(Function(p) If(p Is Nothing, Double.NaN, p.StartTime_h))
                If pulse Is Nothing OrElse Not Double.IsFinite(pulse.StartTime_h) OrElse pulse.StartTime_h < 0.0 OrElse
                   Not Double.IsFinite(pulse.Volume_m3) OrElse pulse.Volume_m3 <= 0.0 OrElse
                   Not Double.IsFinite(pulse.Rate_m3_h) OrElse pulse.Rate_m3_h <= 0.0 Then
                    Throw New InvalidOperationException("Each feed pulse needs a finite start time >= 0 h, volume > 0 m3 and rate > 0 m3/h.")
                End If
                Dim endTime = pulse.StartTime_h + pulse.Volume_m3 / pulse.Rate_m3_h
                If Not Double.IsFinite(endTime) OrElse endTime <= pulse.StartTime_h OrElse endTime > duration_h + timeTolerance Then
                    Throw New InvalidOperationException("Each feed pulse must finish within the fed-batch duration: end = start + volume / rate.")
                End If
                If pulse.StartTime_h < previousEnd - timeTolerance Then
                    Throw New InvalidOperationException("Feed pulses must not overlap. Move the next start to or after the previous pulse's end.")
                End If
                previousEnd = endTime
                total += pulse.Volume_m3
            Next
            If Not Double.IsFinite(total) OrElse Not Double.IsFinite(total / BatchDuration) Then
                Throw New InvalidOperationException("The feed schedule must give a finite total volume and cycle-average flow.")
            End If
            Return total
        End Function

        ''' <summary>Explicit editor action: set a standalone supplemental inlet's cycle-average flow, preserving composition.</summary>
        Public Sub ApplyFeedScheduleToInlet()
            If OperatingMode <> BioReactorMode.FedBatch OrElse Not UseFeedSchedule Then
                Throw New InvalidOperationException("Enable the fed-batch feed schedule first.")
            End If
            Dim total = ValidateFeedSchedule()
            If Not GraphicObject.InputConnectors(0).IsAttached OrElse Not GraphicObject.InputConnectors(1).IsAttached Then
                Throw New InvalidOperationException("Connect the initial charge and supplemental feed streams first.")
            End If
            Dim feed = DirectCast(FlowSheet.SimulationObjects(
                GraphicObject.InputConnectors(1).AttachedConnector.AttachedFrom.Name), MaterialStream)
            If feed.GraphicObject.InputConnectors.Any(Function(c) c.IsAttached) OrElse feed.IsAdjustAttached OrElse feed.IsSpecAttached Then
                Throw New InvalidOperationException(String.Format(CultureInfo.InvariantCulture,
                    "The supplemental feed is controlled upstream. Adjust its source to supply {0:G8} m3/h at reactor conditions, averaged over the full cycle.", total / BatchDuration * 3600.0))
            End If
            Dim firstFeed = DirectCast(FlowSheet.SimulationObjects(
                GraphicObject.InputConnectors(0).AttachedConnector.AttachedFrom.Name), MaterialStream)
            Dim pressure = firstFeed.Phases(0).Properties.pressure.GetValueOrDefault() - DeltaP.GetValueOrDefault()
            If pressure <= 0.0 Then Throw New InvalidOperationException("Set a positive supplemental feed pressure first.")
            Dim temperature = firstFeed.Phases(0).Properties.temperature.GetValueOrDefault()
            If (KineticModel = BioKineticModel.JuliaCoFermentation AndAlso ThermalMode = BioReactorThermalMode.DefinedOutletTemperature) OrElse
               (KineticModel <> BioKineticModel.JuliaCoFermentation AndAlso ReactorOperationMode = OperationMode.OutletTemperature) Then temperature = OutletTemperature
            Dim charge = PrepareFeedAtConditions(feed, temperature, pressure)
            Dim flow = FeedVolumeFlow(charge)
            Dim massFlow = charge.Phases(0).Properties.massflow.GetValueOrDefault() * (total / BatchDuration) / flow
            If Not Double.IsFinite(massFlow) OrElse massFlow <= 0.0 Then
                Throw New InvalidOperationException("Calculate the supplemental feed with a positive flow and valid composition first.")
            End If
            FlowSheet.RegisterSnapshot(SnapshotType.ObjectData, feed)
            feed.SetMassFlow(massFlow)
            feed.SetDirtyStatus(True)
            feed.Calculated = False
            feed.GraphicObject.Calculated = False
            SetDirtyStatus(True)
            Calculated = False
            GraphicObject.Calculated = False
        End Sub

        ''' <summary>Integrated supplemental volume at time_s, evaluated analytically so short pulses are not lost.</summary>
        Public Function GetScheduledFeedVolume(time_s As Double) As Double
            If OperatingMode <> BioReactorMode.FedBatch OrElse Not UseFeedSchedule Then Return 0.0
            Return FeedPulses.Sum(Function(p) p.Rate_m3_h *
                Math.Max(0.0, Math.Min(time_s / 3600.0 - p.StartTime_h, p.Volume_m3 / p.Rate_m3_h)))
        End Function

        ''' <summary>Instantaneous supplemental flow (m3/s). Call after ValidateFeedSchedule.</summary>
        Public Function GetScheduledFeedRate(time_s As Double) As Double
            If OperatingMode <> BioReactorMode.FedBatch OrElse Not UseFeedSchedule Then Return 0.0
            Dim time_h = time_s / 3600.0
            For Each pulse In FeedPulses
                If time_h >= pulse.StartTime_h AndAlso time_h < pulse.StartTime_h + pulse.Volume_m3 / pulse.Rate_m3_h Then Return pulse.Rate_m3_h / 3600.0
            Next
            Return 0.0
        End Function

        Private Function LimitFeedStep(time_s As Double, step_s As Double) As Double
            For Each pulse In FeedPulses
                For Each boundary In {pulse.StartTime_h * 3600.0, (pulse.StartTime_h + pulse.Volume_m3 / pulse.Rate_m3_h) * 3600.0}
                    If boundary > time_s + 0.000000001 AndAlso boundary < time_s + step_s Then step_s = boundary - time_s
                Next
            Next
            Return step_s
        End Function

        Private Function PrepareScheduledFeed(initialFlow As Double, temperature As Double, pressure As Double) As MaterialStream
            Dim total = ValidateFeedSchedule()
            If Not Double.IsFinite(Volume) OrElse Volume <= 0.0 OrElse Not Double.IsFinite(initialFlow) OrElse initialFlow <= 0.0 Then
                Throw New InvalidOperationException("Initial working volume and initial-charge flow must be finite and positive.")
            End If
            If Math.Abs(initialFlow / (Volume / BatchDuration) - 1.0) > 0.000001 Then
                Throw New InvalidOperationException(String.Format(CultureInfo.InvariantCulture,
                    "The first inlet represents the initial charge averaged over the cycle. Set it to {0:G8} m3/h at reactor conditions (initial volume / cultivation duration), using Apply Initial Charge Average to Feed Sources or adjusting its upstream source streams.", Volume / BatchDuration * 3600.0))
            End If
            If Not GraphicObject.InputConnectors(1).IsAttached Then
                Throw New InvalidOperationException("Connect the supplemental feed to the second inlet when using a feed schedule.")
            End If
            Dim feed = DirectCast(FlowSheet.SimulationObjects(
                GraphicObject.InputConnectors(1).AttachedConnector.AttachedFrom.Name), MaterialStream)
            Dim charge = PrepareFeedAtConditions(feed, temperature, pressure)
            Dim flow = FeedVolumeFlow(charge)
            If Math.Abs(flow / (total / BatchDuration) - 1.0) > 0.000001 Then
                Throw New InvalidOperationException(String.Format(CultureInfo.InvariantCulture,
                    "The feed schedule totals {0:G8} m3. Set the supplemental inlet to {1:G8} m3/h at reactor conditions (current {2:G8}), using Apply Schedule Average to Supplemental Inlet or adjusting its upstream source.", total, total / BatchDuration * 3600.0, flow * 3600.0))
            End If
            Return charge
        End Function

        Private Shared Function FeedConcentration(feed As MaterialStream, compound As String) As Double
            If feed Is Nothing OrElse String.IsNullOrEmpty(compound) OrElse Not feed.Phases(0).Compounds.ContainsKey(compound) Then Return 0.0
            Return feed.Phases(0).Compounds(compound).MassFlow.GetValueOrDefault() / FeedVolumeFlow(feed)
        End Function

        Private Function SupplementalSensibleHeat(feedAtReactor As MaterialStream) As Double
            If feedAtReactor Is Nothing Then Return 0.0
            Dim original = DirectCast(FlowSheet.SimulationObjects(
                GraphicObject.InputConnectors(1).AttachedConnector.AttachedFrom.Name), MaterialStream)
            Return original.Phases(0).Properties.massflow.GetValueOrDefault() *
                (original.Phases(0).Properties.enthalpy.GetValueOrDefault() - feedAtReactor.Phases(0).Properties.enthalpy.GetValueOrDefault()) * 1000.0
        End Function

        Private Sub AppendFeedScheduleReport(report As System.Text.StringBuilder, ci As CultureInfo)
            report.AppendLine("Supplemental feed schedule (constant-rate pulses; times in h):")
            For Each pulse In FeedPulses.OrderBy(Function(p) p.StartTime_h)
                report.AppendLine(String.Format(ci, "  Start {0:G8}; amount {1:G8} m3; rate {2:G8} m3/h; duration {3:G8}; end {4:G8}.",
                    pulse.StartTime_h, pulse.Volume_m3, pulse.Rate_m3_h, pulse.Volume_m3 / pulse.Rate_m3_h,
                    pulse.StartTime_h + pulse.Volume_m3 / pulse.Rate_m3_h))
            Next
        End Sub

        Private Function PrepareFeedAtConditions(feed As MaterialStream, temperature As Double, pressure As Double) As MaterialStream
            Dim charge = DirectCast(feed.Clone(), MaterialStream)
            charge.SetFlowsheet(FlowSheet)
            charge.SetPropertyPackage(PropertyPackage)
            charge.Phases(0).Properties.temperature = temperature
            charge.Phases(0).Properties.pressure = pressure
            charge.SpecType = StreamSpec.Temperature_and_Pressure
            charge.DefinedFlow = FlowSpec.Mass
            charge.OverrideSingleCompoundFlashBehavior = True
            PropertyPackage.CurrentMaterialStream = charge
            charge.Calculate(True, True)
            Return charge
        End Function

        Private Shared Function FeedVolumeFlow(feed As MaterialStream) As Double
            Dim flow = feed.Phases(1).Properties.volumetric_flow.GetValueOrDefault()
            If flow <= 0.0 Then flow = feed.Phases(0).Properties.volumetric_flow.GetValueOrDefault()
            If Not Double.IsFinite(flow) OrElse flow <= 0.0 Then
                Throw New InvalidOperationException("Connected feed streams must have positive volumetric flow at the reactor temperature.")
            End If
            Return flow
        End Function

    End Class

End Namespace
