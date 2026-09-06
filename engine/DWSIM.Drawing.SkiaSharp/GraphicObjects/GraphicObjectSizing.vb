Imports System.Collections.Generic
Imports System.Linq
Imports DWSIM.Interfaces
Imports DWSIM.Interfaces.Enums
Imports DWSIM.Interfaces.Enums.GraphicObjects

Namespace GraphicObjects

    ''' <summary>Relative symbol sizes shared by the canvas and palette thumbnails.</summary>
    Public NotInheritable Class GraphicObjectSizing

        Private Sub New()
        End Sub

        Public Shared Function GetScale(objectType As ObjectType,
                                        Optional objectClass As SimulationObjectClass = SimulationObjectClass.None) As Double
            Select Case objectType
                'Streams and live instruments have their own aspect ratios and text layout.
                Case ObjectType.MaterialStream, ObjectType.EnergyStream, ObjectType.OT_InformationCarrier,
                     ObjectType.Input, ObjectType.AnalogGauge, ObjectType.DigitalGauge, ObjectType.LevelGauge
                    Return 1.0
                Case ObjectType.Valve, ObjectType.OrificePlate
                    Return 0.6
                Case ObjectType.Pump
                    Return 0.72
                Case ObjectType.Compressor, ObjectType.Expander, ObjectType.CompressorExpander, ObjectType.Pipe
                    Return 0.8
                Case ObjectType.Controller_PID, ObjectType.Controller_Python, ObjectType.Controller_MPC
                    Return 0.64
                Case ObjectType.OT_Adjust, ObjectType.OT_Spec, ObjectType.OT_Recycle,
                     ObjectType.OT_EnergyRecycle, ObjectType.Switch
                    Return 0.56
                Case ObjectType.NodeIn, ObjectType.NodeOut, ObjectType.Mixer, ObjectType.Splitter, ObjectType.EnergyMixer
                    Return 0.72
                Case ObjectType.HeatExchanger
                    'Allow for the shorter visible height of the heat exchanger artwork.
                    Return 1.4
                Case ObjectType.RCT_Conversion, ObjectType.RCT_Equilibrium, ObjectType.RCT_Gibbs,
                     ObjectType.RCT_CSTR, ObjectType.RCT_PFR, ObjectType.RCT_GibbsReaktoro
                    Return 1.2
                Case ObjectType.ShortcutColumn, ObjectType.DistillationColumn, ObjectType.AbsorptionColumn,
                     ObjectType.RefluxedAbsorber, ObjectType.ReboiledAbsorber
                    Return 1.4
            End Select

            'External unit operations share a graphic type; their class supplies the size tier.
            Select Case objectClass
                Case SimulationObjectClass.PressureChangers
                    Return 0.8
                Case SimulationObjectClass.Controllers
                    Return 0.64
                Case SimulationObjectClass.Logical, SimulationObjectClass.Switches
                    Return 0.56
                Case SimulationObjectClass.MixersSplitters
                    Return 0.72
                Case SimulationObjectClass.Reactors
                    Return 1.2
                Case SimulationObjectClass.Columns
                    Return 1.4
                Case Else
                    Return 1.0
            End Select
        End Function

        ''' <summary>Applies the recommended sizes to an existing layout as one undoable action.</summary>
        Public Shared Function ApplyDefaultSizes(flowsheet As IFlowsheet, objects As IEnumerable(Of IGraphicObject)) As Integer
            If flowsheet Is Nothing OrElse objects Is Nothing Then Return 0

            Dim changes As New List(Of (Graphic As IGraphicObject, Size As Integer))
            For Each graphic In objects.Distinct()
                If graphic Is Nothing OrElse graphic.Owner Is Nothing OrElse graphic.IsConnector Then Continue For

                Select Case graphic.ObjectType
                    'These graphics have independent aspect ratios or editable live readouts.
                    Case ObjectType.MaterialStream, ObjectType.EnergyStream, ObjectType.OT_InformationCarrier,
                         ObjectType.Input, ObjectType.AnalogGauge, ObjectType.DigitalGauge, ObjectType.LevelGauge,
                         ObjectType.CapeOpenUO
                        Continue For
                End Select

                Dim size = CInt(50 * GetScale(graphic.ObjectType, graphic.Owner.ObjectClass))
                If graphic.Width <> size OrElse graphic.Height <> size Then changes.Add((graphic, size))
            Next

            If changes.Count = 0 Then Return 0

            flowsheet.RegisterSnapshot(SnapshotType.ObjectLayout)
            For Each change In changes
                Dim graphic = change.Graphic
                'Keep each symbol centred on its existing location, including rotated symbols.
                graphic.X += (graphic.Width - change.Size) / 2.0F
                graphic.Y += (graphic.Height - change.Size) / 2.0F
                graphic.Width = change.Size
                graphic.Height = change.Size
            Next
            For Each change In changes
                change.Graphic.PositionConnectors()
            Next

            Return changes.Count
        End Function

    End Class

End Namespace
