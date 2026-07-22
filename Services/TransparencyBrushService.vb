Imports Avalonia
Imports Avalonia.Media
Imports SkiaSharp
Imports System.Collections.Generic
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
        ' Pfad-Schluessel: siehe PathIdentity - auf Linux sind Pfade case-sensitiv, und zwei
        ' Dateien, die sich nur darin unterscheiden, teilten sich sonst das Transparenz-Ergebnis.
        Private Shared ReadOnly _alphaCache As New Dictionary(Of String, Boolean)(PathIdentity.Comparer)
        Private Shared ReadOnly _alphaPending As New HashSet(Of String)(PathIdentity.Comparer)
        ''' Callbacks von Aufrufern, die auf eine BEREITS LAUFENDE Berechnung desselben Schlüssels
        ''' warten (Audit 2026-07-22): der zweite Aufrufer (z.B. Editor, während der Viewer dieselbe
        ''' Datei rechnet) bekam vorher weder Wert noch Benachrichtigung - sein Binding blieb auf
        ''' dem Default stehen, bis irgendein Zufalls-Re-Read kam.
        Private Shared ReadOnly _alphaWaiters As New Dictionary(Of String, List(Of Action))(PathIdentity.Comparer)
        Private Shared ReadOnly _alphaLock As New Object()
        ' Grober Deckel gegen unbegrenztes Wachstum über lange Sitzungen; bei Überlauf wird komplett
        ' geleert (die Berechnung ist nach dem Roh-Puffer-Umbau billig genug für Wiederholung).
        Private Const AlphaCacheLimit As Integer = 512

        ' Dateiformate, die strukturell keinen Alphakanal besitzen können (JPEG hat schlicht keine
        ' Transparenz-Unterstützung) bzw. bei denen die Vorschau immer aus einer opaken Quelle
        ' erzeugt wird (eingebettete RAW-JPEG-Vorschau, Video-Frame). Für sie ist ein Schachbrett-/
        ' Volltonfarbe-Hintergrund unter dem Bild grundsätzlich bedeutungslos - er kann bei diesen
        ' Formaten nie etwas "durchscheinen" lassen, sondern nur an Rundungs-/Letterbox-Rändern
        ' fälschlich sichtbar werden. Alle anderen (unbekannten) Formate gelten konservativ weiterhin
        ' als potenziell transparent.
        ''' RAW-Endungen kommen aus RawPreviewService.SupportedExtensions - eine eigene Kopie
        ''' wuerde beim naechsten neuen Format vergessen (siehe Kommentar dort).
        Private Shared ReadOnly OpaqueOnlyExtensions As String() = {
            ".jpg", ".jpeg", ".jpe", ".jfif", ".bmp",
            ".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v"
        }.Concat(RawPreviewService.SupportedExtensions).ToArray()

        Public Shared Function CanHaveTransparency(filePath As String) As Boolean
            If String.IsNullOrEmpty(filePath) Then Return True
            Dim ext = IO.Path.GetExtension(filePath).ToLowerInvariant()
            Return Not OpaqueOnlyExtensions.Contains(ext)
        End Function

        ''' <summary>Cache-freundlicher, UI-Thread-tauglicher Zugriff: Ist die Antwort sofort bekannt
        ''' (Formats-Shortcut, Cache-Treffer), liefert die Funktion True und füllt hasTransparency.
        ''' Sonst False - die teure Berechnung (Bild dekodieren + Alpha-Scan) läuft dann EINMAL im
        ''' Hintergrund, und onComputed wird anschließend auf dem UI-Thread aufgerufen (typisch:
        ''' RaisePropertyChanged des Brush-Getters).
        '''
        ''' Hintergrund (Analyse 2026-07-16): Der frühere synchrone HasVisibleTransparency-Aufruf
        ''' sass in Binding-Gettern von Viewer UND Editor - ein grosses PNG wurde dabei KOMPLETT
        ''' auf dem UI-Thread dekodiert und per GetPixel (Interop pro Pixel!) gescannt: sekundenlange
        ''' Haenger beim ersten Anzeigen. Verdaechtig nah am unbestaetigten "Viewer-Start-Haenger".</summary>
        Public Shared Function TryGetTransparency(filePath As String, ByRef hasTransparency As Boolean,
                                                  onComputed As Action) As Boolean
            If String.IsNullOrWhiteSpace(filePath) Then hasTransparency = True : Return True
            If Not CanHaveTransparency(filePath) Then hasTransparency = False : Return True
            If Not IO.File.Exists(filePath) Then hasTransparency = True : Return True

            Dim key As String
            Try
                Dim info = New IO.FileInfo(filePath)
                key = $"{filePath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}"
            Catch
                hasTransparency = True
                Return True
            End Try

            SyncLock _alphaLock
                Dim cached As Boolean = False
                If _alphaCache.TryGetValue(key, cached) Then
                    hasTransparency = cached
                    Return True
                End If
                If _alphaPending.Contains(key) Then
                    ' Berechnung läuft schon - den Callback DIESES Aufrufers registrieren, damit
                    ' auch er nach Abschluss sein PropertyChanged bekommt (statt ihn zu verlieren).
                    If onComputed IsNot Nothing Then
                        Dim waiters As List(Of Action) = Nothing
                        If Not _alphaWaiters.TryGetValue(key, waiters) Then
                            waiters = New List(Of Action)()
                            _alphaWaiters(key) = waiters
                        End If
                        waiters.Add(onComputed)
                    End If
                    Return False
                End If
                _alphaPending.Add(key)
            End SyncLock

            System.Threading.Tasks.Task.Run(Sub()
                                         Dim value = ComputeHasTransparency(filePath)
                                         Dim pendingWaiters As List(Of Action) = Nothing
                                         SyncLock _alphaLock
                                             If _alphaCache.Count >= AlphaCacheLimit Then _alphaCache.Clear()
                                             _alphaCache(key) = value
                                             _alphaPending.Remove(key)
                                             If _alphaWaiters.TryGetValue(key, pendingWaiters) Then _alphaWaiters.Remove(key)
                                         End SyncLock
                                         If onComputed IsNot Nothing OrElse (pendingWaiters IsNot Nothing AndAlso pendingWaiters.Count > 0) Then
                                             Avalonia.Threading.Dispatcher.UIThread.Post(Sub()
                                                                                             onComputed?.Invoke()
                                                                                             If pendingWaiters IsNot Nothing Then
                                                                                                 For Each waiter In pendingWaiters
                                                                                                     waiter()
                                                                                                 Next
                                                                                             End If
                                                                                         End Sub)
                                         End If
                                     End Sub)
            Return False
        End Function

        Private Shared Function ComputeHasTransparency(filePath As String) As Boolean
            Try
                Using bitmap = SKBitmap.Decode(filePath)
                    If bitmap Is Nothing OrElse bitmap.ColorType = SKColorType.Unknown Then Return True
                    ' Dekoder weiss es am besten: als opak markierte Bilder haben nie sichtbare
                    ' Transparenz - kein Pixel-Scan noetig.
                    If bitmap.AlphaType = SKAlphaType.Opaque Then Return False
                    Select Case bitmap.ColorType
                        Case SKColorType.Rgb565, SKColorType.Rgb888x, SKColorType.Gray8
                            Return False
                    End Select

                    If (bitmap.ColorType = SKColorType.Bgra8888 OrElse bitmap.ColorType = SKColorType.Rgba8888) AndAlso
                       bitmap.GetPixels() <> IntPtr.Zero Then
                        ' Alpha liegt bei beiden Formaten in Byte 3 - zeilenweise Rohkopie statt
                        ' GetPixel (Interop pro Pixel machte den Scan bei grossen PNGs sekundenlang).
                        Dim width = bitmap.Width
                        Dim row(width * 4 - 1) As Byte
                        Dim basePtr = bitmap.GetPixels()
                        For y = 0 To bitmap.Height - 1
                            Runtime.InteropServices.Marshal.Copy(IntPtr.Add(basePtr, y * bitmap.RowBytes), row, 0, width * 4)
                            For x = 0 To width - 1
                                If row(x * 4 + 3) < 250 Then Return True
                            Next
                        Next
                        Return False
                    End If

                    For y = 0 To bitmap.Height - 1
                        For x = 0 To bitmap.Width - 1
                            If bitmap.GetPixel(x, y).Alpha < 250 Then Return True
                        Next
                    Next
                    Return False
                End Using
            Catch
                Return True
            End Try
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
