Imports Avalonia
Imports Avalonia.Media
Imports System.Linq

Namespace Services

    ''' Liefert den Hintergrund-Brush für transparente Bildbereiche in Viewer und Editor, passend
    ''' zur Einstellung SettingsViewModel.TransparencyBackgroundMode/-Color. Das Schachbrettmuster
    ''' (Standard) wird als kachelnder DrawingBrush einmalig aufgebaut und wiederverwendet, da es
    ''' unabhängig von der Nutzereinstellung immer identisch aussieht.
    Public NotInheritable Class TransparencyBrushService
        Private Sub New()
        End Sub

        Private Const TileSize As Double = 16.0

        Private Shared ReadOnly _checkerboardBrush As IBrush = BuildCheckerboardBrush()

        ' Dateiformate, die strukturell keinen Alphakanal besitzen können (JPEG hat schlicht keine
        ' Transparenz-Unterstützung) bzw. bei denen die Vorschau immer aus einer opaken Quelle
        ' erzeugt wird (eingebettete RAW-JPEG-Vorschau, Video-Frame). Für sie ist ein Schachbrett-/
        ' Volltonfarbe-Hintergrund unter dem Bild grundsätzlich bedeutungslos - er kann bei diesen
        ' Formaten nie etwas "durchscheinen" lassen, sondern nur an Rundungs-/Letterbox-Rändern
        ' fälschlich sichtbar werden. Alle anderen (unbekannten) Formate gelten konservativ weiterhin
        ' als potenziell transparent.
        Private Shared ReadOnly OpaqueOnlyExtensions As String() = {
            ".jpg", ".jpeg", ".jpe", ".jfif", ".bmp",
            ".cr2", ".cr3", ".nef", ".arw", ".dng", ".pef", ".rw2",
            ".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v"
        }

        Public Shared Function CanHaveTransparency(filePath As String) As Boolean
            If String.IsNullOrEmpty(filePath) Then Return True
            Dim ext = IO.Path.GetExtension(filePath).ToLowerInvariant()
            Return Not OpaqueOnlyExtensions.Contains(ext)
        End Function

        Public Shared Function GetBrush(mode As String, colorHex As String) As IBrush
            If String.Equals(mode, "Solid", StringComparison.OrdinalIgnoreCase) Then
                Try
                    Return New SolidColorBrush(Color.Parse(colorHex))
                Catch
                    Return New SolidColorBrush(Colors.White)
                End Try
            End If
            If String.Equals(mode, "None", StringComparison.OrdinalIgnoreCase) Then
                Return Brushes.Transparent
            End If
            Return _checkerboardBrush
        End Function

        Private Shared Function BuildCheckerboardBrush() As IBrush
            Dim light = New SolidColorBrush(Color.FromRgb(214, 214, 214))
            Dim dark = New SolidColorBrush(Color.FromRgb(158, 158, 158))
            Dim half = TileSize / 2.0

            Dim group As New DrawingGroup()
            group.Children.Add(New GeometryDrawing() With {
                .Brush = light,
                .Geometry = New RectangleGeometry(New Rect(0, 0, TileSize, TileSize))
            })
            group.Children.Add(New GeometryDrawing() With {
                .Brush = dark,
                .Geometry = New RectangleGeometry(New Rect(0, 0, half, half))
            })
            group.Children.Add(New GeometryDrawing() With {
                .Brush = dark,
                .Geometry = New RectangleGeometry(New Rect(half, half, half, half))
            })

            Return New DrawingBrush(group) With {
                .TileMode = TileMode.Tile,
                .Stretch = Stretch.None,
                .DestinationRect = New RelativeRect(New Rect(0, 0, TileSize, TileSize), RelativeUnit.Absolute)
            }
        End Function

    End Class

End Namespace
