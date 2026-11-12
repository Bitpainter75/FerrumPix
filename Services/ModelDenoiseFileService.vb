Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.IO.Compression
Imports System.Linq
Imports System.Text
Imports System.Threading.Tasks

Namespace Services

    ' Das Ergebnis des Entrauschens mit Modell als Datei neben der RAW: "foto.cr2" -> "foto.cr2.fpdnz".
    '
    ' WAS DRIN STEHT. Die entrauschten LINEAREN 16-Bit-Daten, wie sie RawDecodeServiceModelDenoise
    ' im Speicher haelt (sechs Byte je Bildpunkt, vor Belichtung und Tonkurve). Keine Mischung: die
    ' Staerke entsteht weiter beim Umsetzen aus verrauscht und entrauscht, und die verrauschten Daten
    ' liefert LibRaw in Sekunden. Deshalb bleibt die Staerke nach dem Laden frei, und das Modell
    ' laeuft nicht noch einmal.
    '
    ' WANN SIE GILT. Der Kopf traegt einen Schluessel aus Groesse und Aenderungszeit der RAW, Demosaic,
    ' Entklemmen, Farbweg und Modelldatei (RawDecodeService.DenoiseFileKeyFor), NICHT den Pfad: die
    ' Datei wandert mit der RAW. Passt der Schluessel nicht, wird sie nicht benutzt, und das Modell
    ' rechnet wie ohne sie; beim naechsten Speichern wird sie ersetzt.
    '
    ' AUFBAU. "FPDNZ", ein Byte Fassung, Breite, Hoehe, Schluessel (Text mit Laengenvorsatz), Zahl der
    ' Baender, die Tabelle ihrer gepackten Laengen, dann die Baender selbst. Die Tabelle steht vorn,
    ' damit schon der Kopf sagt, ob die Datei vollstaendig ist. Ein Band sind BandRows Zeilen; je
    ' Zeile und Kanal steht der Unterschied zum linken Nachbarn (wie der Filter "Sub" in PNG), die
    ' unteren und oberen Bytes getrennt in zwei Ebenen, dann Deflate in der staerksten Stufe. Die
    ' Baender packen und entpacken unabhaengig voneinander und deshalb parallel.
    '
    ' GEMESSEN an der Pruef-RAW (EOS M50, ISO 12800, 24 MP, Rohdaten 139 MB): Deflate schnell und
    ' Bytes abwechselnd 102 MB, mit Byte-Ebenen 91 MB, dazu die staerkste Stufe 63 MB (46 %). Das
    ' Schreiben dauert dann 1,6 s im Hintergrund, das Oeffnen mit der Datei samt Entwickeln 3 s
    ' statt 70 s Modell auf der Karte. Die staerkste Stufe kostet nur beim Schreiben.
    '
    ' WANN SIE ENTSTEHT. Nur beim bewussten Speichern (Editor) und bei Export und Stapel, und nur mit
    ' der Einstellung AppSettings.KeepModelDenoiseFiles. Geloescht wird sie beim Speichern ohne
    ' Entrauschen und mit der RAW (RawSidecarService.AccompanyDelete). Das Abschalten der Einstellung
    ' laesst vorhandene Dateien liegen und lesbar; es werden nur keine neuen geschrieben.

    Public NotInheritable Class ModelDenoiseFileService

        Private Sub New()
        End Sub

        Public Const Extension As String = ".fpdnz"
        Private Const FormatVersion As Byte = 1
        Private Shared ReadOnly Magic As Byte() = Encoding.ASCII.GetBytes("FPDNZ")
        ''' <summary>Zeilen je Band. 128 Zeilen sind bei 6000 Punkten Breite 4,6 MB, genug fuer
        ''' Deflate und klein genug, dass alle Kerne zu tun haben.</summary>
        Private Const BandRows As Integer = 128

        Public Shared Function PathFor(rawPath As String) As String
            Return rawPath & Extension
        End Function

        Public Shared Function Exists(rawPath As String) As Boolean
            Return Not String.IsNullOrWhiteSpace(rawPath) AndAlso File.Exists(PathFor(rawPath))
        End Function

        ''' <summary>Ist die Einstellung an? Ohne sie wird nichts geschrieben; gelesen wird eine
        ''' vorhandene Datei trotzdem, sie kostet nur Sekundenbruchteile und erspart das Modell.</summary>
        Public Shared ReadOnly Property Enabled As Boolean
            Get
                Return AppSettingsService.Load().KeepModelDenoiseFiles
            End Get
        End Property

        ''' <summary>Die Daten schreiben, atomar ueber eine Zwischendatei. False bei jedem Fehler (kein
        ''' Schreibrecht, Platte voll); die Bearbeitung selbst haengt nicht daran.</summary>
        Public Shared Function TryWrite(rawPath As String, key As String, width As Integer, height As Integer,
                                        data As Byte()) As Boolean
            If String.IsNullOrWhiteSpace(rawPath) OrElse String.IsNullOrEmpty(key) OrElse data Is Nothing Then Return False
            If width <= 0 OrElse height <= 0 OrElse CLng(data.Length) <> CLng(width) * height * 6 Then Return False
            Dim target = PathFor(rawPath)
            Dim temp = target & ".tmp"
            Try
                Dim bandCount = (height + BandRows - 1) \ BandRows
                Dim bands = New Byte(bandCount - 1)() {}
                Parallel.For(0, bandCount, Sub(b) bands(b) = PackBand(data, width, height, b))
                Using stream As New FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20)
                    Using writer As New BinaryWriter(stream, Encoding.UTF8, leaveOpen:=True)
                        writer.Write(Magic)
                        writer.Write(FormatVersion)
                        writer.Write(width)
                        writer.Write(height)
                        writer.Write(key)
                        ' Erst die Tabelle der Laengen, dann die Baender: so prueft schon der Kopf,
                        ' ob die Datei vollstaendig ist (ReadHeader).
                        writer.Write(bandCount)
                        For Each band In bands
                            writer.Write(band.Length)
                        Next
                        For Each band In bands
                            writer.Write(band)
                        Next
                    End Using
                End Using
                File.Move(temp, target, overwrite:=True)
                Return True
            Catch ex As Exception
                DiagnosticLogService.LogException("ModelDenoiseFile.Write", ex)
                Try
                    If File.Exists(temp) Then File.Delete(temp)
                Catch
                End Try
                Return False
            End Try
        End Function

        ''' <summary>Die Daten lesen, wenn die Datei da ist und ihr Schluessel passt; sonst Nothing.
        ''' Eine beschaedigte Datei ist kein Fehler fuer den Nutzer: dann rechnet das Modell.</summary>
        Public Shared Function TryRead(rawPath As String, key As String, ByRef width As Integer, ByRef height As Integer) As Byte()
            width = 0
            height = 0
            If String.IsNullOrWhiteSpace(rawPath) OrElse String.IsNullOrEmpty(key) Then Return Nothing
            Dim source = PathFor(rawPath)
            If Not File.Exists(source) Then Return Nothing
            Try
                Using stream As New FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20)
                    Using reader As New BinaryReader(stream, Encoding.UTF8, leaveOpen:=True)
                        Dim w = 0, h = 0
                        Dim lengths As Integer() = Nothing
                        If Not ReadHeader(reader, stream, w, h, key, lengths) Then Return Nothing
                        Dim bandCount = lengths.Length
                        Dim packed = New Byte(bandCount - 1)() {}
                        For b = 0 To bandCount - 1
                            packed(b) = reader.ReadBytes(lengths(b))
                            If packed(b).Length <> lengths(b) Then Return Nothing
                        Next
                        Dim data(CInt(CLng(w) * h * 6) - 1) As Byte
                        Dim broken = False
                        Parallel.For(0, bandCount, Sub(b)
                                                       If Not UnpackBand(packed(b), data, w, h, b) Then broken = True
                                                   End Sub)
                        If broken Then Return Nothing
                        width = w
                        height = h
                        Return data
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("ModelDenoiseFile.Read", ex)
                Return Nothing
            End Try
        End Function

        ''' <summary>Liegt eine Datei mit passendem Schluessel da, und ist sie VOLLSTAENDIG? Liest nur
        ''' Kopf und Laengentabelle, entpackt nichts. Was das nicht findet, ist ein Bruch mitten in
        ''' den Daten; den meldet erst Deflate beim Lesen (Pruefsumme je Band), und der Ausgabeweg
        ''' rechnet dann nach (ImageProcessor.LoadRecipeModelDenoiseFile).</summary>
        Public Shared Function Matches(rawPath As String, key As String) As Boolean
            If String.IsNullOrWhiteSpace(rawPath) OrElse String.IsNullOrEmpty(key) Then Return False
            Dim source = PathFor(rawPath)
            If Not File.Exists(source) Then Return False
            Try
                Using stream As New FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read)
                    Using reader As New BinaryReader(stream, Encoding.UTF8, leaveOpen:=True)
                        Dim w = 0, h = 0
                        Dim lengths As Integer() = Nothing
                        Return ReadHeader(reader, stream, w, h, key, lengths)
                    End Using
                End Using
            Catch
                Return False
            End Try
        End Function

        ''' <summary>Kopf und Laengentabelle lesen. True nur, wenn der Schluessel passt, die Zahl der
        ''' Baender zur Hoehe passt und die Datei genau so lang ist, wie die Tabelle sagt: eine
        ''' abgeschnittene (abgebrochene Kopie) oder verlaengerte Datei faellt hier schon auf.</summary>
        Private Shared Function ReadHeader(reader As BinaryReader, stream As Stream, ByRef width As Integer,
                                           ByRef height As Integer, key As String, ByRef lengths As Integer()) As Boolean
            Dim magicRead = reader.ReadBytes(Magic.Length)
            If Not magicRead.SequenceEqual(Magic) Then Return False
            If reader.ReadByte() <> FormatVersion Then Return False
            width = reader.ReadInt32()
            height = reader.ReadInt32()
            If width <= 0 OrElse height <= 0 OrElse CLng(width) * height * 6 > Integer.MaxValue Then Return False
            If Not KeyMatches(reader.ReadString(), key) Then Return False
            Dim bandCount = reader.ReadInt32()
            If bandCount <> (height + BandRows - 1) \ BandRows Then Return False
            lengths = New Integer(bandCount - 1) {}
            Dim total = 0L
            For b = 0 To bandCount - 1
                lengths(b) = reader.ReadInt32()
                If lengths(b) <= 0 Then Return False
                total += lengths(b)
            Next
            Return stream.Position + total = stream.Length
        End Function

        ''' <summary>Steht am Ende des erwarteten Schluessels: der Modellteil zaehlt nicht (das Modell
        ''' fehlt gerade, siehe RawDecodeService.DenoiseFileKeyFor).</summary>
        Public Const AnyModel As String = "*"

        ''' <summary>Gleich, oder bei <see cref="AnyModel"/> gleich bis dorthin. Der Teil der RAW hat
        ''' eine feste Zahl Felder, der Vergleich bis zum Trennzeichen davor ist also eindeutig.</summary>
        Public Shared Function KeyMatches(stored As String, expected As String) As Boolean
            If stored Is Nothing OrElse expected Is Nothing Then Return False
            If expected.EndsWith("|" & AnyModel, StringComparison.Ordinal) Then
                Dim prefix = expected.Substring(0, expected.Length - AnyModel.Length)
                Return stored.StartsWith(prefix, StringComparison.Ordinal)
            End If
            Return String.Equals(stored, expected, StringComparison.Ordinal)
        End Function

        ''' <summary>Ein Band: je Zeile und Kanal der Unterschied zum linken Nachbarn, dann Deflate.</summary>
        Private Shared Function PackBand(data As Byte(), width As Integer, height As Integer, band As Integer) As Byte()
            Dim firstRow = band * BandRows
            Dim rows = Math.Min(BandRows, height - firstRow)
            Dim rowBytes = width * 6
            Dim delta(rows * rowBytes - 1) As Byte
            Dim half = rows * width * 3
            For r = 0 To rows - 1
                Dim src = (firstRow + r) * rowBytes
                Dim dst = r * width * 3
                Dim prevR = 0, prevG = 0, prevB = 0
                For x = 0 To width - 1
                    Dim p = src + x * 6
                    Dim q = dst + x * 3
                    Dim vr = CInt(data(p)) Or (CInt(data(p + 1)) << 8)
                    Dim vg = CInt(data(p + 2)) Or (CInt(data(p + 3)) << 8)
                    Dim vb = CInt(data(p + 4)) Or (CInt(data(p + 5)) << 8)
                    WriteDelta(delta, q, half, vr - prevR)
                    WriteDelta(delta, q + 1, half, vg - prevG)
                    WriteDelta(delta, q + 2, half, vb - prevB)
                    prevR = vr
                    prevG = vg
                    prevB = vb
                Next
            Next
            Using packed As New MemoryStream()
                Using deflate As New ZLibStream(packed, CompressionLevel.SmallestSize, leaveOpen:=True)
                    deflate.Write(delta, 0, delta.Length)
                End Using
                Return packed.ToArray()
            End Using
        End Function

        ''' <summary>Byte-Ebenen: erst alle unteren Bytes des Bandes, dann alle oberen. Die oberen
        ''' sind nach dem Unterschied zum Nachbarn fast immer 0 oder 255 und packen dann sehr gut;
        ''' abwechselnd mit den unruhigen unteren sah Deflate davon wenig.</summary>
        Private Shared Sub WriteDelta(target As Byte(), sample As Integer, half As Integer, value As Integer)
            Dim v = value And &HFFFF
            target(sample) = CByte(v And &HFF)
            target(half + sample) = CByte(v >> 8)
        End Sub

        Private Shared Function UnpackBand(packed As Byte(), data As Byte(), width As Integer, height As Integer,
                                           band As Integer) As Boolean
            Dim firstRow = band * BandRows
            Dim rows = Math.Min(BandRows, height - firstRow)
            Dim rowBytes = width * 6
            Dim delta(rows * rowBytes - 1) As Byte
            Try
                Using input As New MemoryStream(packed)
                    Using inflate As New ZLibStream(input, CompressionMode.Decompress)
                        inflate.ReadExactly(delta, 0, delta.Length)
                    End Using
                End Using
            Catch
                Return False
            End Try
            Dim half = rows * width * 3
            For r = 0 To rows - 1
                Dim src = r * width * 3
                Dim dst = (firstRow + r) * rowBytes
                Dim prevR = 0, prevG = 0, prevB = 0
                For x = 0 To width - 1
                    Dim p = src + x * 3
                    Dim q = dst + x * 6
                    prevR = (prevR + (CInt(delta(p)) Or (CInt(delta(half + p)) << 8))) And &HFFFF
                    prevG = (prevG + (CInt(delta(p + 1)) Or (CInt(delta(half + p + 1)) << 8))) And &HFFFF
                    prevB = (prevB + (CInt(delta(p + 2)) Or (CInt(delta(half + p + 2)) << 8))) And &HFFFF
                    data(q) = CByte(prevR And &HFF)
                    data(q + 1) = CByte(prevR >> 8)
                    data(q + 2) = CByte(prevG And &HFF)
                    data(q + 3) = CByte(prevG >> 8)
                    data(q + 4) = CByte(prevB And &HFF)
                    data(q + 5) = CByte(prevB >> 8)
                Next
            Next
            Return True
        End Function

        ''' <summary>Die Datei zu dieser RAW entfernen, falls es eine gibt. False nur, wenn sie da
        ''' ist und nicht weg geht.</summary>
        Public Shared Function Delete(rawPath As String) As Boolean
            If String.IsNullOrWhiteSpace(rawPath) Then Return True
            Dim target = PathFor(rawPath)
            Try
                If File.Exists(target) Then File.Delete(target)
                Return True
            Catch ex As Exception
                DiagnosticLogService.LogException("ModelDenoiseFile.Delete", ex)
                Return False
            End Try
        End Function

    End Class

End Namespace
