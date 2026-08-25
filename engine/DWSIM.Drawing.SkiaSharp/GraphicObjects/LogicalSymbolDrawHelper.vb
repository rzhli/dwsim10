'    LogicalSymbolDrawHelper - plain P&ID-style instrument bubble used by the
'    controller/switch graphics when the flowsheet is not in the photorealistic
'    Color-Icons theme (DrawMode 2). Keeps Logical objects readable on the canvas
'    instead of stretching their photographic palette artwork over the block.

Namespace GraphicObjects

    Public Module LogicalSymbolDrawHelper

        Public Sub DrawBubble(canvas As SKCanvas, x As Double, y As Double, w As Double, h As Double,
                              label As String, stroke As SKColor, fore As SKColor,
                              Optional dashed As Boolean = False)

            Dim cx = CSng(x + w / 2.0)
            Dim cy = CSng(y + h / 2.0)
            Dim r = CSng(Math.Max(Math.Min(w, h) / 2.0 - 2.0, 4.0))

            Using p As New SKPaint With {.IsAntialias = True, .IsStroke = True, .StrokeWidth = 2.0F, .Color = stroke}
                If dashed Then
                    p.PathEffect = SKPathEffect.CreateDash(New Single() {6.0F, 4.0F}, 1)
                End If
                canvas.DrawCircle(cx, cy, r, p)
            End Using

            Using tp As New SKPaint With {.IsAntialias = True, .Color = fore,
                                          .Typeface = SKTypeface.FromFamilyName("Arial", SKTypefaceStyle.Bold)}
                tp.TextSize = CSng(r * 0.75)
                Dim tw = tp.MeasureText(label)
                If tw > r * 1.7F Then
                    tp.TextSize *= CSng(r * 1.7 / tw)
                    tw = tp.MeasureText(label)
                End If
                canvas.DrawText(label, cx - tw / 2.0F, cy + tp.TextSize * 0.35F, tp)
            End Using

        End Sub

    End Module

End Namespace
