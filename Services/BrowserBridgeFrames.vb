Imports System
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.Globalization
Imports System.Linq
Imports System.Net.WebSockets
Imports System.Numerics
Imports System.Reflection
Imports System.Runtime.InteropServices
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Headless
Imports Avalonia.Media.Imaging
Imports Avalonia.Threading
Imports SkiaSharp

Namespace Services

    ' Die Bilder der Fenster: abholen, mit dem zuletzt gesendeten Stand vergleichen, die geaenderten
    ' Ausschnitte kodieren und schicken.
    '
    ' VERLUSTBEHAFTET IN DER BEWEGUNG, VERLUSTFREI IM STILLSTAND. Ein grosser Ausschnitt geht als
    ' JPEG hinaus, damit Reglerzuege fluessig bleiben. Steht das Fenster danach kurz still, folgt
    ' derselbe Bereich noch einmal als PNG. Was im Browser steht, ist dann bitgleich das, was die
    ' Anwendung gezeichnet hat - fuer eine Bildbearbeitung ist das der Punkt des Ganzen.
    Partial Public NotInheritable Class BrowserBridge

        ''' <summary>Abstand der Abholung in Millisekunden, also hoechstens rund dreissig Bilder je
        ''' Sekunde.</summary>
        Private Const FrameIntervalMilliseconds As Integer = 33

        ''' <summary>So lange muss ein Fenster stillstehen, bevor der verlustfreie Nachzug kommt.</summary>
        Private Const LosslessDelayMilliseconds As Long = 300

        ''' <summary>Bis zu dieser Flaeche geht ein Ausschnitt gleich verlustfrei hinaus: ein
        ''' blinkender Textzeiger oder ein Knopf unter der Maus ist als PNG kleiner als als JPEG
        ''' und braucht dann keinen Nachzug.</summary>
        Private Const DirectLosslessPixels As Integer = 96 * 96

        Private Const JpegQuality As Integer = 85

        Private Const HeaderLength As Integer = 22

        ''' <summary>Kantenlaenge der Kacheln, in denen Unterschiede gesucht werden.</summary>
        Private Const TileSize As Integer = 64

        ''' <summary>Mehr Rechtecke als das lohnen den Aufwand je Nachricht nicht mehr; dann geht
        ''' das eine Rechteck hinaus, das alle umschliesst.</summary>
        Private Const MaxRects As Integer = 24

        ''' <summary>Ein Ausschnitt wird in Streifen von etwa dieser Flaeche zerlegt, die
        ''' nebeneinander kodiert werden. Ein ganzes Fenster belegt sonst einen Kern fuer die volle
        ''' Dauer, waehrend die uebrigen warten.</summary>
        Private Const StripPixels As Integer = 256 * 1024

        Private NotInheritable Class BridgeWindow
            Public Id As Integer
            ''' <summary>Der Stand, den der Browser hat.</summary>
            Public Previous As Integer()
            ''' <summary>Arbeitspuffer fuer das neue Bild; tauscht nach dem Vergleich mit Previous.</summary>
            Public Scratch As Integer()
            Public PixelWidth As Integer
            Public PixelHeight As Integer
            ''' <summary>Das Bildobjekt der Plattform, das zuletzt verglichen wurde. Ist es noch
            ''' dasselbe, hat die Anwendung nichts neu gezeichnet.</summary>
            Public LastFrame As Object
            Public HasLossy As Boolean
            Public LossyLeft As Integer
            Public LossyTop As Integer
            Public LossyRight As Integer
            Public LossyBottom As Integer
            Public LastChange As Long
            Public ActivationOrder As Integer
            ''' <summary>Reihenfolge der Farbkanaele im Bild der Plattform. Sie haengt davon ab, wie
            ''' das Toolkit aufgesetzt wurde, und wird deshalb je Bild abgelesen statt angenommen.</summary>
            Public ColorType As SKColorType = SKColorType.Bgra8888
        End Class

        Private NotInheritable Class FrameJob
            Public WindowId As Integer
            Public X As Integer
            Public Y As Integer
            Public Width As Integer
            Public Height As Integer
            Public Pixels As Integer()
            Public Lossless As Boolean
            Public ColorType As SKColorType
        End Class

        Private Shared ReadOnly States As New Dictionary(Of Window, BridgeWindow)()
        Private Shared _framePump As DispatcherTimer
        Private Shared _nextWindowId As Integer
        Private Shared _activationCounter As Integer
        Private Shared _lastLayout As String
        Private Shared _sending As Integer
        Private Shared _scale As Double = 1.0
        Private Shared _lastFrameField As FieldInfo
        Private Shared _lastFrameFieldSearched As Boolean

        Private Shared Sub StartFramePump()
            If _framePump IsNot Nothing Then Return
            ' LOADED UND NICHT BACKGROUND: die Abholung muss vor den Eingaben an die Reihe kommen.
            ' Beim Ziehen eines Reglers steht staendig eine Mausbewegung in der Schlange; mit
            ' niedrigerer Prioritaet kaeme die Abholung erst dran, wenn die Maus ruht, und der
            ' Browser saehe gerade waehrend der Bewegung nichts.
            _framePump = New DispatcherTimer(DispatcherPriority.Loaded) With {
                .Interval = TimeSpan.FromMilliseconds(FrameIntervalMilliseconds)}
            AddHandler _framePump.Tick, AddressOf OnFrameTick
            _framePump.Start()
        End Sub

        Private Shared Sub StopFramePump()
            If _framePump Is Nothing Then Return
            _framePump.Stop()
            RemoveHandler _framePump.Tick, AddressOf OnFrameTick
            _framePump = Nothing
            States.Clear()
            _lastLayout = Nothing
            _pointerOwner = Nothing
        End Sub

        ''' <summary>Ein Browser hat sich verbunden: seine Flaeche und sein Pixelverhaeltnis
        ''' uebernehmen und alles neu schicken, er hat noch nichts.</summary>
        Private Shared Sub OnClientConnected(hello As JsonElement)
            For Each state In States.Values
                state.Previous = Nothing
                state.LastFrame = Nothing
                state.HasLossy = False
            Next
            _lastLayout = Nothing
            _lastCursor = Nothing
            _pointerOwner = Nothing
            ApplyViewport(hello)
        End Sub

        ''' <summary>Das Hauptfenster fuellt den Tab, und gezeichnet wird in dessen Pixelverhaeltnis:
        ''' auf einem hochaufloesenden Schirm ist das Bild damit scharf statt hochgerechnet.</summary>
        Private Shared Sub ApplyViewport(message As JsonElement)
            Dim width = Math.Clamp(ReadNumber(message, "w", 1280), 320, 8192)
            Dim height = Math.Clamp(ReadNumber(message, "h", 800), 240, 8192)
            _scale = Math.Clamp(ReadNumber(message, "scale", 1), 0.5, 4)

            Dim windows = VisibleWindows()
            For Each win In windows
                InvokeHeadless(win, "SetRenderScaling", _scale)
            Next

            Dim main = windows.FirstOrDefault(Function(w) w.Owner Is Nothing)
            If main Is Nothing Then Return
            main.WindowState = Avalonia.Controls.WindowState.Normal
            main.Position = New PixelPoint(0, 0)
            main.Width = Math.Max(width, main.MinWidth)
            main.Height = Math.Max(height, main.MinHeight)
        End Sub

        Private Shared Function VisibleWindows() As List(Of Window)
            Dim source = WindowSource
            If source Is Nothing Then Return New List(Of Window)()
            Return source().Where(Function(w) w IsNot Nothing AndAlso w.IsVisible AndAlso w.PlatformImpl IsNot Nothing).ToList()
        End Function

        Private Shared Function StateOf(win As Window) As BridgeWindow
            Dim state As BridgeWindow = Nothing
            If States.TryGetValue(win, state) Then Return state

            _nextWindowId += 1
            _activationCounter += 1
            state = New BridgeWindow With {.Id = _nextWindowId, .ActivationOrder = _activationCounter}
            States(win) = state
            ' Welches Fenster oben liegt, weiss ohne Fensterverwaltung niemand ausser uns: gemerkt
            ' wird die Reihenfolge, in der sie zuletzt aktiviert wurden.
            AddHandler win.Activated,
                Sub(sender, e)
                    _activationCounter += 1
                    state.ActivationOrder = _activationCounter
                End Sub
            If _scale <> 1.0 Then InvokeHeadless(win, "SetRenderScaling", _scale)
            Return state
        End Function

        Private Shared Sub OnFrameTick(sender As Object, e As EventArgs)
            If CurrentClient Is Nothing Then Return
            ' Solange der letzte Schwung noch kodiert oder gesendet wird, kommt kein neuer dazu.
            ' Verglichen wird immer gegen den GESENDETEN Stand, es geht also nichts verloren: was
            ' sich inzwischen geaendert hat, steckt im naechsten Ausschnitt.
            If Volatile.Read(_sending) <> 0 Then
                _skippedTicks += 1
                Return
            End If
            Try
                Dim started = Stopwatch.GetTimestamp()
                Dim windows = VisibleWindows()
                For Each closed In States.Keys.Where(Function(w) Not windows.Contains(w)).ToList()
                    States.Remove(closed)
                Next

                Dim jobs As New List(Of FrameJob)()
                For Each win In windows
                    CollectFrame(win, StateOf(win), jobs)
                Next

                Dim layout = BuildLayout(windows)
                Dim layoutChanged = Not String.Equals(layout, _lastLayout, StringComparison.Ordinal)
                If Not layoutChanged AndAlso jobs.Count = 0 Then Return
                _lastLayout = layout

                Interlocked.Exchange(_sending, 1)
                Dim layoutToSend = If(layoutChanged, layout, Nothing)
                Dim collectTicks = Stopwatch.GetTimestamp() - started
                Dim ignored = Task.Run(Function() SendFramesAsync(layoutToSend, jobs, collectTicks))
            Catch ex As Exception
                DiagnosticLogService.LogException("BrowserBridge.Frame", ex)
            End Try
        End Sub

        ''' <summary>Lage, Groesse und Reihenfolge aller Fenster in Bildpunkten. Der Browser legt
        ''' danach je Fenster ein Canvas an.</summary>
        Private Shared Function BuildLayout(windows As List(Of Window)) As String
            Dim text As New StringBuilder()
            text.Append("{""t"":""windows"",""scale"":").Append(_scale.ToString("R", CultureInfo.InvariantCulture)).Append(",""list"":[")
            Dim first = True
            For Each win In windows
                Dim state = StateOf(win)
                Dim size = PixelSize.FromSize(win.ClientSize, win.RenderScaling)
                If Not first Then text.Append(","c)
                first = False
                text.Append("{""id"":").Append(state.Id).
                     Append(",""x"":").Append(win.Position.X).
                     Append(",""y"":").Append(win.Position.Y).
                     Append(",""w"":").Append(size.Width).
                     Append(",""h"":").Append(size.Height).
                     Append(",""z"":").Append(state.ActivationOrder).
                     Append(",""s"":").Append(win.RenderScaling.ToString("R", CultureInfo.InvariantCulture)).
                     Append("}"c)
            Next
            text.Append("]}")
            Return text.ToString()
        End Function

        ''' <summary>Holt das zuletzt gezeichnete Bild eines Fensters und legt die geaenderten
        ''' Ausschnitte als Auftraege ab. Hat die Anwendung nichts neu gezeichnet, prueft es nur,
        ''' ob ein verlustfreier Nachzug faellig ist.</summary>
        Private Shared Sub CollectFrame(win As Window, state As BridgeWindow, jobs As List(Of FrameJob))
            Dim marker = CurrentFrameMarker(win)
            If marker IsNot Nothing AndAlso marker Is state.LastFrame Then
                CollectLossless(state, jobs)
                Return
            End If

            ' Das Bild der Plattform wird direkt gelesen, wenn es erreichbar ist. Der oeffentliche
            ' Weg legt erst eine Kopie des ganzen Bilds an, die hier gleich wieder verworfen wuerde.
            ' Gezeichnet wird auf demselben Faden, das Bild kann sich waehrenddessen nicht aendern.
            Dim direct = TryCast(marker, WriteableBitmap)
            Dim copy As WriteableBitmap = Nothing
            If direct Is Nothing Then
                copy = win.GetLastRenderedFrame()
                If copy Is Nothing Then Return
            End If
            Try
                Using locked = If(direct, copy).Lock()
                    state.ColorType = If(locked.Format = Avalonia.Platform.PixelFormat.Rgba8888,
                                         SKColorType.Rgba8888, SKColorType.Bgra8888)
                    Dim width = locked.Size.Width
                    Dim height = locked.Size.Height
                    If width <= 0 OrElse height <= 0 Then Return

                    Dim count = width * height
                    If state.Scratch Is Nothing OrElse state.Scratch.Length <> count Then state.Scratch = New Integer(count - 1) {}
                    If locked.RowBytes = width * 4 Then
                        Marshal.Copy(locked.Address, state.Scratch, 0, count)
                    Else
                        For row = 0 To height - 1
                            Marshal.Copy(IntPtr.Add(locked.Address, row * locked.RowBytes), state.Scratch, row * width, width)
                        Next
                    End If

                    Dim sameSize = state.Previous IsNot Nothing AndAlso state.PixelWidth = width AndAlso state.PixelHeight = height
                    Dim rects As List(Of PixelRect)
                    If sameSize Then
                        rects = FindChangedRects(state.Previous, state.Scratch, width, height)
                    Else
                        rects = New List(Of PixelRect) From {New PixelRect(0, 0, width, height)}
                        state.HasLossy = False
                    End If

                    ' Der Arbeitspuffer wird zum gesendeten Stand, der alte zum Arbeitspuffer.
                    Dim swap = state.Previous
                    state.Previous = state.Scratch
                    state.Scratch = swap
                    state.PixelWidth = width
                    state.PixelHeight = height
                    state.LastFrame = marker

                    If rects.Count = 0 Then
                        CollectLossless(state, jobs)
                        Return
                    End If

                    state.LastChange = Environment.TickCount64
                    For Each rect In rects
                        Dim lossless = rect.Width * rect.Height <= DirectLosslessPixels
                        AddJobs(state, rect, lossless, jobs)
                        If lossless Then Continue For
                        If state.HasLossy Then
                            state.LossyLeft = Math.Min(state.LossyLeft, rect.X)
                            state.LossyTop = Math.Min(state.LossyTop, rect.Y)
                            state.LossyRight = Math.Max(state.LossyRight, rect.Right - 1)
                            state.LossyBottom = Math.Max(state.LossyBottom, rect.Bottom - 1)
                        Else
                            state.LossyLeft = rect.X
                            state.LossyTop = rect.Y
                            state.LossyRight = rect.Right - 1
                            state.LossyBottom = rect.Bottom - 1
                            state.HasLossy = True
                        End If
                    Next
                End Using
            Finally
                copy?.Dispose()
            End Try
        End Sub

        ''' <summary>Der verlustfreie Nachzug: steht das Fenster still, geht der Bereich, der
        ''' zuletzt als JPEG hinausging, noch einmal als PNG.</summary>
        Private Shared Sub CollectLossless(state As BridgeWindow, jobs As List(Of FrameJob))
            If Not state.HasLossy OrElse state.Previous Is Nothing Then Return
            If Environment.TickCount64 - state.LastChange < LosslessDelayMilliseconds Then Return
            state.HasLossy = False
            Dim left = Math.Clamp(state.LossyLeft, 0, state.PixelWidth - 1)
            Dim top = Math.Clamp(state.LossyTop, 0, state.PixelHeight - 1)
            Dim right = Math.Clamp(state.LossyRight, left, state.PixelWidth - 1)
            Dim bottom = Math.Clamp(state.LossyBottom, top, state.PixelHeight - 1)
            AddJobs(state, New PixelRect(left, top, right - left + 1, bottom - top + 1), True, jobs)
        End Sub

        ''' <summary>Schneidet einen Ausschnitt aus dem gesendeten Stand und zerlegt ihn in
        ''' waagerechte Streifen, die nebeneinander kodiert werden koennen.</summary>
        Private Shared Sub AddJobs(state As BridgeWindow, rect As PixelRect, lossless As Boolean, jobs As List(Of FrameJob))
            Dim stripHeight = Math.Max(TileSize, StripPixels \ Math.Max(1, rect.Width))
            Dim top = rect.Y
            While top < rect.Bottom
                Dim height = Math.Min(stripHeight, rect.Bottom - top)
                ' Ein schmaler Rest am Ende geht mit dem letzten Streifen, statt eine eigene
                ' Nachricht zu werden.
                If rect.Bottom - (top + height) < TileSize \ 2 Then height = rect.Bottom - top
                Dim pixels = New Integer(rect.Width * height - 1) {}
                For row = 0 To height - 1
                    Array.Copy(state.Previous, (top + row) * state.PixelWidth + rect.X, pixels, row * rect.Width, rect.Width)
                Next
                jobs.Add(New FrameJob With {
                    .WindowId = state.Id, .X = rect.X, .Y = top, .Width = rect.Width, .Height = height,
                    .Pixels = pixels, .Lossless = lossless, .ColorType = state.ColorType})
                top += height
            End While
        End Sub

        ''' <summary>Die Rechtecke, in denen sich zwei gleich grosse Bilder unterscheiden.
        '''
        ''' Gesucht wird in Kacheln. Benachbarte geaenderte Kacheln einer Zeile werden zu einem
        ''' Lauf, gleiche Laeufe untereinander zu einem Rechteck. Zieht jemand einen Regler am
        ''' rechten Rand, aendern sich der Regler und das Bild in der Mitte: das sind dann zwei
        ''' Rechtecke und nicht eines, das auch alles dazwischen einschliesst.</summary>
        Friend Shared Function FindChangedRects(previous As Integer(), current As Integer(),
                                                width As Integer, height As Integer) As List(Of PixelRect)
            Dim columns = (width + TileSize - 1) \ TileSize
            Dim rows = (height + TileSize - 1) \ TileSize
            Dim result As New List(Of PixelRect)()
            ' Offene Rechtecke der vorigen Kachelzeile, nach ihrer ersten Spalte.
            Dim open As New Dictionary(Of Integer, PixelRect)()
            Dim dirty = New Boolean(columns - 1) {}

            For tileRow = 0 To rows - 1
                Array.Clear(dirty, 0, columns)
                Dim top = tileRow * TileSize
                Dim bottom = Math.Min(top + TileSize, height)
                Dim remaining = columns
                For y = top To bottom - 1
                    If remaining = 0 Then Exit For
                    Dim offset = y * width
                    For column = 0 To columns - 1
                        If dirty(column) Then Continue For
                        Dim left = column * TileSize
                        If SegmentDiffers(previous, current, offset + left, Math.Min(TileSize, width - left)) Then
                            dirty(column) = True
                            remaining -= 1
                        End If
                    Next
                Next

                Dim continued As New Dictionary(Of Integer, PixelRect)()
                Dim first = 0
                While first < columns
                    If Not dirty(first) Then
                        first += 1
                        Continue While
                    End If
                    Dim last = first
                    While last + 1 < columns AndAlso dirty(last + 1)
                        last += 1
                    End While
                    Dim left = first * TileSize
                    Dim runWidth = Math.Min((last + 1) * TileSize, width) - left
                    Dim above As PixelRect
                    If open.TryGetValue(first, above) AndAlso above.Width = runWidth Then
                        continued(first) = New PixelRect(left, above.Y, runWidth, bottom - above.Y)
                        open.Remove(first)
                    Else
                        continued(first) = New PixelRect(left, top, runWidth, bottom - top)
                    End If
                    first = last + 1
                End While
                result.AddRange(open.Values)
                open = continued
            Next
            result.AddRange(open.Values)

            If result.Count > MaxRects Then
                Dim union = result(0)
                For Each rect In result
                    union = union.Union(rect)
                Next
                result.Clear()
                result.Add(union)
            End If
            Return result
        End Function

        ''' <summary>Ob sich zwei Bilder in einem Abschnitt einer Zeile unterscheiden. Verglichen
        ''' wird in Vektoren, also acht Bildpunkte auf einmal, wo der Prozessor das kann.</summary>
        Private Shared Function SegmentDiffers(previous As Integer(), current As Integer(), start As Integer, length As Integer) As Boolean
            Dim index = start
            Dim limit = start + length
            Dim stride = System.Numerics.Vector(Of Integer).Count
            While index + stride <= limit
                If Not System.Numerics.Vector.EqualsAll(New System.Numerics.Vector(Of Integer)(previous, index),
                                                        New System.Numerics.Vector(Of Integer)(current, index)) Then Return True
                index += stride
            End While
            While index < limit
                If previous(index) <> current(index) Then Return True
                index += 1
            End While
            Return False
        End Function

        ''' <summary>Das Bildobjekt, das die Plattform zuletzt gezeichnet hat. Sie legt je Bild ein
        ''' neues an; ist es noch dasselbe wie beim letzten Mal, muss nichts kopiert und nichts
        ''' verglichen werden. Das Feld ist intern. Findet es sich nicht mehr, wird jedes Mal
        ''' verglichen: langsamer, aber richtig.</summary>
        Private Shared Function CurrentFrameMarker(win As Window) As Object
            Dim impl = win.PlatformImpl
            If impl Is Nothing Then Return Nothing
            If Not _lastFrameFieldSearched Then
                _lastFrameFieldSearched = True
                _lastFrameField = impl.GetType().GetField("_lastRenderedFrame", BindingFlags.Instance Or BindingFlags.NonPublic)
            End If
            If _lastFrameField Is Nothing OrElse Not _lastFrameField.DeclaringType.IsInstanceOfType(impl) Then Return Nothing
            Return _lastFrameField.GetValue(impl)
        End Function

        Private Shared Async Function SendFramesAsync(layout As String, jobs As List(Of FrameJob), collectTicks As Long) As Task
            Try
                Dim started = Stopwatch.GetTimestamp()
                Dim messages = New Byte(jobs.Count - 1)() {}
                If jobs.Count = 1 Then
                    messages(0) = Encode(jobs(0))
                ElseIf jobs.Count > 1 Then
                    Parallel.For(0, jobs.Count, Sub(index) messages(index) = Encode(jobs(index)))
                End If
                Dim encoded = Stopwatch.GetTimestamp()

                If layout IsNot Nothing Then Await SendTextAsync(layout)
                Dim bytes As Long = 0
                For Each message In messages
                    bytes += message.Length
                    Await SendAsync(message, WebSocketMessageType.Binary)
                Next
                RecordTiming(collectTicks, encoded - started, Stopwatch.GetTimestamp() - encoded, jobs, bytes)
            Catch ex As Exception
                DiagnosticLogService.LogException("BrowserBridge.Send", ex)
            Finally
                Interlocked.Exchange(_sending, 0)
            End Try
        End Function

        ''' <summary>Eine Bildnachricht: 22 Bytes Kopf (Art, Fenster, Lage, Groesse, Format), danach
        ''' die kodierten Bilddaten.</summary>
        Private Shared Function Encode(job As FrameJob) As Byte()
            Dim handle = GCHandle.Alloc(job.Pixels, GCHandleType.Pinned)
            Try
                ' Das Bild der Plattform ist vormultipliziert. Fuer JPEG gilt es als deckend, PNG
                ' behaelt die Deckkraft, damit runde Fensterecken durchsichtig bleiben.
                Dim info As New SKImageInfo(job.Width, job.Height, job.ColorType,
                                            If(job.Lossless, SKAlphaType.Premul, SKAlphaType.Opaque))
                Using pixmap As New SKPixmap(info, handle.AddrOfPinnedObject(), job.Width * 4)
                    Using data = If(job.Lossless,
                                    pixmap.Encode(New SKPngEncoderOptions(SKPngEncoderFilterFlags.Sub, 1)),
                                    pixmap.Encode(New SKJpegEncoderOptions(JpegQuality)))
                        Dim image = data.ToArray()
                        Dim message = New Byte(HeaderLength + image.Length - 1) {}
                        message(0) = 1
                        WriteInt32(message, 1, job.WindowId)
                        WriteInt32(message, 5, job.X)
                        WriteInt32(message, 9, job.Y)
                        WriteInt32(message, 13, job.Width)
                        WriteInt32(message, 17, job.Height)
                        message(21) = If(job.Lossless, CByte(1), CByte(0))
                        Buffer.BlockCopy(image, 0, message, HeaderLength, image.Length)
                        Return message
                    End Using
                End Using
            Finally
                handle.Free()
            End Try
        End Function

        Private Shared Sub WriteInt32(target As Byte(), offset As Integer, value As Integer)
            Dim bytes = BitConverter.GetBytes(value)
            If Not BitConverter.IsLittleEndian Then Array.Reverse(bytes)
            Buffer.BlockCopy(bytes, 0, target, offset, 4)
        End Sub

        ' ------------------------------------------------------------------------------------
        ' Zeitmessung fuer das Diagnoseprotokoll (Bereich "BrowserBridge.Timing"). Sie sammelt
        ' ueber zwei Sekunden und schreibt dann eine Zeile: wie viele Schwuenge hinausgingen, wie
        ' lange Abholen und Vergleichen, Kodieren und Senden im Mittel dauerten, wie viel Flaeche
        ' und wie viele Bytes je Schwung, und wie oft die Abholung aussetzen musste, weil der
        ' vorige Schwung noch unterwegs war. Geschrieben wird nur bei eingeschaltetem Protokoll.
        ' ------------------------------------------------------------------------------------
        Private Shared ReadOnly TimingLock As New Object()
        Private Shared _timingStart As Long
        Private Shared _timingBatches As Integer
        Private Shared _timingCollect As Long
        Private Shared _timingEncode As Long
        Private Shared _timingSend As Long
        Private Shared _timingPixels As Long
        Private Shared _timingBytes As Long
        Private Shared _timingLossless As Integer
        Private Shared _skippedTicks As Integer

        Private Shared Sub RecordTiming(collectTicks As Long, encodeTicks As Long, sendTicks As Long,
                                        jobs As List(Of FrameJob), bytes As Long)
            Dim line As String = Nothing
            SyncLock TimingLock
                Dim now = Environment.TickCount64
                If _timingStart = 0 Then _timingStart = now
                _timingBatches += 1
                _timingCollect += collectTicks
                _timingEncode += encodeTicks
                _timingSend += sendTicks
                _timingBytes += bytes
                For Each job In jobs
                    _timingPixels += CLng(job.Width) * job.Height
                    If job.Lossless Then _timingLossless += 1
                Next
                If now - _timingStart < 2000 Then Return

                Dim toMilliseconds = 1000.0 / Stopwatch.Frequency / _timingBatches
                Dim seconds = (now - _timingStart) / 1000.0
                line = String.Format(CultureInfo.InvariantCulture,
                    "batches/s={0:F1} collect={1:F1}ms encode={2:F1}ms send={3:F1}ms megapixels/batch={4:F2} kB/batch={5:F0} lossless={6} skipped={7}",
                    _timingBatches / seconds, _timingCollect * toMilliseconds, _timingEncode * toMilliseconds,
                    _timingSend * toMilliseconds, _timingPixels / 1000000.0 / _timingBatches,
                    _timingBytes / 1024.0 / _timingBatches, _timingLossless, Interlocked.Exchange(_skippedTicks, 0))
                _timingStart = now
                _timingBatches = 0
                _timingCollect = 0
                _timingEncode = 0
                _timingSend = 0
                _timingPixels = 0
                _timingBytes = 0
                _timingLossless = 0
            End SyncLock
            DiagnosticLogService.LogAlways("BrowserBridge.Timing", line)
        End Sub
    End Class
End Namespace
