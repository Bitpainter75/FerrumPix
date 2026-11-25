Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Text.RegularExpressions
Imports MetadataExtractor.Formats.Jpeg
Imports MetadataExtractor.Formats.Xmp
Imports XmpCore

Namespace Services

    ''' <summary>Was die Erkennung ueber eine Datei sagt.</summary>
    Public Enum MotionPhotoState
        ''' Das XMP behauptet nichts: ein gewoehnliches Bild.
        None = 0
        ''' Das XMP sagt Ja, und am errechneten Versatz beginnt wirklich ein Film.
        Present = 1
        ''' Das XMP sagt Ja, aber der Film fehlt, ist abgeschnitten oder die Angaben passen nicht
        ''' zur Datei. So sieht eine Datei aus, deren Bild neu kodiert und deren altes XMP
        ''' uebernommen wurde.
        ClaimedButMissing = 2
    End Enum

    ''' <summary>Das Ergebnis der Erkennung. Versatz und Laenge gelten nur bei
    ''' <see cref="MotionPhotoState.Present"/>, sonst stehen sie auf 0. Sie werden nirgends
    ''' gespeichert: jedes Schreiben von Metadaten in den Kopf der Datei verschiebt den Versatz.</summary>
    Public Structure MotionPhotoInfo
        Public State As MotionPhotoState
        ''' Wo der Film in der Datei beginnt, gezaehlt vom Dateianfang.
        Public VideoOffset As Long
        ''' Wie lang der Film ist.
        Public VideoLength As Long

        ''' <summary>Sagt das XMP, dass die Datei ein Motion Photo ist (mit oder ohne Film)?</summary>
        Public ReadOnly Property IsClaimed As Boolean
            Get
                Return State <> MotionPhotoState.None
            End Get
        End Property

        ''' <summary>Ist der Film wirklich da?</summary>
        Public ReadOnly Property HasVideo As Boolean
            Get
                Return State = MotionPhotoState.Present
            End Get
        End Property
    End Structure

    ''' <summary>Was das XMP einer Datei ueber einen angehaengten Film behauptet, noch ohne Blick in
    ''' die Datei. Zahlen, die fehlen oder sich nicht lesen lassen, stehen auf -1.</summary>
    Public NotInheritable Class MotionPhotoClaim
        ''' MotionPhoto=1 oder MicroVideo=1.
        Public Property Claimed As Boolean
        ''' Die Laenge des Verzeichniseintrags "MotionPhoto".
        Public Property DirectoryVideoLength As Long = -1
        ''' Die Laenge dieses Eintrags samt seiner Polsterung und allem, was im Verzeichnis nach ihm
        ''' steht: der Abstand seines Anfangs vom Dateiende.
        Public Property DirectoryTailLength As Long = -1
        ''' MicroVideoOffset der aelteren Schreibweise: der Abstand des Films vom Dateiende und
        ''' damit zugleich seine Laenge.
        Public Property MicroVideoOffset As Long = -1
    End Class

    ''' <summary>
    ''' Erkennt ein Motion Photo: ein JPEG, an das die Kamera einen kurzen Film (MP4) angehaengt hat.
    ''' Gekennzeichnet ist es im XMP, neu mit MotionPhoto=1 und einem Verzeichnis, dessen Eintrag
    ''' "MotionPhoto" die Laenge des Films traegt, aelter mit MicroVideo=1 und MicroVideoOffset als
    ''' Abstand vom Dateiende.
    '''
    ''' WARUM ES DAS GIBT: Wer das JPEG neu kodiert, schreibt nur das Bild. Der Film dahinter ging
    ''' beim Speichern im Editor und beim Drehen im Betrachter still verloren, das uebernommene XMP
    ''' behauptete ihn aber weiter, mit einer Laenge, zu der es keine Bytes mehr gab. Deshalb fragen
    ''' die Speicherwege hier nach (RawSidecarService.SavesRecipeBeside,
    ''' ImageProcessor.CanReplaceOriginal, SaveImage): ein Motion Photo mit Film wird nie neu
    ''' kodiert an Ort und Stelle, seine Bearbeitung geht ins Rezept daneben.
    '''
    ''' DER DATEINAME BEWEIST NICHTS: es gibt MVIMG_*.jpg ohne Film, und Motion Photos mit jedem
    ''' anderen Namen. Entschieden wird am XMP und an den Bytes.
    '''
    ''' DIE ERKENNUNG IST EINE REINE FUNKTION UND VERTRAEGT JEDE DATEI: sie wirft nie, legt kein
    ''' Feld in der Groesse einer Angabe aus der Datei an und liest hinter dem Kopf des JPEG genau
    ''' zwoelf Bytes. Alle Laengen sind Long und werden gegen die Dateilaenge geprueft, BEVOR
    ''' addiert wird: ein Ueberlauf wirft in VB, und eine Angabe aus einer fremden Datei darf das
    ''' nicht ausloesen.
    '''
    ''' NICHT ERKANNT werden Dateien ohne dieses XMP (ein Hersteller legt den Film nur mit einer
    ''' eigenen Marke am Dateiende ab) und Filme in HEIC. Fuer sie gilt der Schutz nicht; HEIC wird
    ''' ohnehin nie an Ort und Stelle geschrieben.
    '''
    ''' ZUM ABSPIELEN im Betrachter gibt es hier die Adresse, unter der der Spieler den Film direkt
    ''' aus der Datei liest (<see cref="SubfileAddress"/>), und fuer Spieler, die das nicht koennen,
    ''' den Rueckfall: der Filmteil als eigene Datei im Cache der Anwendung
    ''' (<see cref="ExtractClip"/>, <see cref="ClearClipCache"/>). Nur dieser Rueckfall liest den
    ''' Film selbst und schreibt etwas, und nie neben das Foto.
    ''' </summary>
    Public NotInheritable Class MotionPhotoService

        Private Sub New()
        End Sub

        ' Die Namensraeume der Felder. Sie gehoeren zum Dateiformat; welches Kuerzel eine Datei
        ' dafuer waehlt, ist frei, deshalb wird ueberall am Namensraum erkannt.
        Private Const CameraNamespace As String = "http://ns.google.com/photos/1.0/camera/"
        Private Const ContainerNamespace As String = "http://ns.google.com/photos/1.0/container/"
        Private Const ItemNamespace As String = "http://ns.google.com/photos/1.0/container/item/"

        ''' <summary>Der Eintrag des Verzeichnisses, der den Film beschreibt.</summary>
        Private Const MotionSemantic As String = "MotionPhoto"

        ''' <summary>Vor dem Film steht ein ganzes JPEG samt dem XMP, das ihn ankuendigt. Ein
        ''' Versatz darunter zeigt in den Kopf der Datei und nicht auf einen Film.</summary>
        Friend Const MinimumImageLength As Long = 256

        ''' <summary>Ein Film von weniger als ein paar Kilobyte ist keiner: schon Kopf und
        ''' Inhaltsverzeichnis eines MP4 mit einem einzigen Bild brauchen mehr.</summary>
        Friend Const MinimumVideoLength As Long = 2048

        ''' <summary>Mehr Eintraege fuehrt kein Verzeichnis (ueblich sind zwei oder drei). Die Grenze
        ''' haelt eine Datei, die Tausende ankuendigt, davon ab, hier Arbeit zu machen.</summary>
        Private Const MaximumDirectoryItems As Integer = 32

        ''' <summary>Die Felder, die einen angehaengten Film behaupten. Sie entfallen in jeder neu
        ''' geschriebenen Datei, siehe <see cref="StripMotionFields"/>.</summary>
        Private Shared ReadOnly MotionFieldNames As String() = {
            "MotionPhoto", "MotionPhotoVersion", "MotionPhotoPresentationTimestampUs",
            "MicroVideo", "MicroVideoVersion", "MicroVideoOffset", "MicroVideoPresentationTimestampUs"}

        Private Shared ReadOnly JpegExtensions As String() = {".jpg", ".jpeg", ".jpe", ".jfif"}

        ''' <summary>Traegt der Pfad eine JPEG-Endung? Nur solche Dateien gelten hier als Motion
        ''' Photo; die Erkennung im EXIF-Lauf (LivePhotoService.Identify) fragt dasselbe.</summary>
        Friend Shared Function IsJpegPath(path As String) As Boolean
            If String.IsNullOrEmpty(path) Then Return False
            Return JpegExtensions.Contains(IO.Path.GetExtension(path).ToLowerInvariant())
        End Function

        ' ── Schritt 1: was das XMP behauptet ──────────────────────────────────────

        ''' <summary>Liest die Behauptung aus den Verzeichnissen des Metadatenlesers (dieselben, die
        ''' der EXIF-Lauf ohnehin hat). Nie Nothing; ohne XMP oder ohne die Felder ist
        ''' <see cref="MotionPhotoClaim.Claimed"/> falsch.</summary>
        Public Shared Function ReadClaim(directories As IEnumerable(Of MetadataExtractor.Directory)) As MotionPhotoClaim
            Dim merged As New MotionPhotoClaim()
            If directories Is Nothing Then Return merged
            Try
                For Each xmpDirectory In directories.OfType(Of XmpDirectory)()
                    Dim claim = ReadClaim(xmpDirectory.XmpMeta)
                    If Not claim.Claimed Then Continue For
                    merged.Claimed = True
                    If merged.DirectoryVideoLength < 0 Then
                        merged.DirectoryVideoLength = claim.DirectoryVideoLength
                        merged.DirectoryTailLength = claim.DirectoryTailLength
                    End If
                    If merged.MicroVideoOffset < 0 Then merged.MicroVideoOffset = claim.MicroVideoOffset
                Next
            Catch ex As Exception
                DiagnosticLogService.LogException("MotionPhoto.ReadClaim", ex)
            End Try
            Return merged
        End Function

        ''' <summary>Liest die Behauptung aus einem XMP-Paket. Nie Nothing.</summary>
        Public Shared Function ReadClaim(meta As IXmpMeta) As MotionPhotoClaim
            Dim claim As New MotionPhotoClaim()
            If meta Is Nothing Then Return claim
            Try
                Dim motion = IsOne(ReadProperty(meta, CameraNamespace, "MotionPhoto"))
                Dim micro = IsOne(ReadProperty(meta, CameraNamespace, "MicroVideo"))
                If Not motion AndAlso Not micro Then Return claim
                claim.Claimed = True
                claim.MicroVideoOffset = ParseLength(ReadProperty(meta, CameraNamespace, "MicroVideoOffset"))
                ReadDirectory(meta, claim)
            Catch ex As Exception
                ' Ein kaputtes Paket ist wie eines ohne Verzeichnis: was bis hierher gelesen ist, gilt.
                DiagnosticLogService.LogException("MotionPhoto.ReadClaim", ex)
            End Try
            Return claim
        End Function

        Private Shared Function ReadProperty(meta As IXmpMeta, ns As String, name As String) As String
            Try
                Return meta.GetProperty(ns, name)?.Value
            Catch
                ' Ein Namensraum, den das Paket nicht kennt, oder ein kaputter Eintrag: wie keiner.
                Return Nothing
            End Try
        End Function

        Private Shared Function IsOne(value As String) As Boolean
            Return String.Equals(If(value, "").Trim(), "1", StringComparison.Ordinal)
        End Function

        ''' <summary>Eine Laenge aus dem XMP. -1 fuer alles, was keine nicht negative ganze Zahl im
        ''' Bereich eines Long ist; eine Zahl mit dreissig Stellen faellt also hier schon heraus.</summary>
        Private Shared Function ParseLength(value As String) As Long
            Dim parsed As Long
            If Not Long.TryParse(If(value, "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, parsed) Then Return -1
            Return parsed
        End Function

        ' Ein Feld eines Verzeichniseintrags, wie der Durchlauf des Pakets es nennt, etwa
        ' "Container:Directory[2]/Container:Item/Item:Length". Die Kuerzel davor sind frei.
        Private Shared ReadOnly DirectoryFieldPattern As New Regex(
            "^[^:/\[\]]+:Directory\[(\d{1,4})\]/(?:[^/]+/)?[^:/\[\]]+:(Semantic|Length|Padding)$",
            RegexOptions.CultureInvariant)

        ''' <summary>Liest das Verzeichnis und traegt die Laenge des Eintrags "MotionPhoto" und den
        ''' Abstand seines Anfangs vom Dateiende ein.
        '''
        ''' Die Eintraege stehen in der Reihenfolge, in der ihre Daten in der Datei liegen: zuerst
        ''' das Bild selbst (dessen Laenge 0 heissen darf), dann jeder weitere hintereinander bis
        ''' zum Dateiende. Die Lage eines Eintrags ergibt sich deshalb vom ENDE her: seine Laenge,
        ''' seine Polsterung und alles, was nach ihm kommt. Das bleibt richtig, wenn jemand
        ''' Metadaten in den Kopf der Datei schreibt und alles dahinter verschiebt.
        '''
        ''' Fehlt bei einem Eintrag ab dem Film die Laenge oder ist sie keine Zahl, bleibt der
        ''' Abstand auf -1: geraten wird nicht.</summary>
        Private Shared Sub ReadDirectory(meta As IXmpMeta, claim As MotionPhotoClaim)
            Dim prefix = XmpMetaFactory.SchemaRegistry.GetNamespacePrefix(ContainerNamespace)
            If String.IsNullOrEmpty(prefix) Then Return

            Dim semantics As New Dictionary(Of Integer, String)()
            Dim lengths As New Dictionary(Of Integer, String)()
            Dim paddings As New Dictionary(Of Integer, String)()
            For Each info In meta.Properties
                Dim path = info?.Path
                If String.IsNullOrEmpty(path) OrElse Not path.StartsWith(prefix & "Directory[", StringComparison.Ordinal) Then Continue For
                Dim match = DirectoryFieldPattern.Match(path)
                If Not match.Success Then Continue For
                Dim index = Integer.Parse(match.Groups(1).Value, CultureInfo.InvariantCulture)
                ' Ein Verzeichnis mit mehr Eintraegen, als es je gibt: gar nicht erst auswerten.
                If index < 1 OrElse index > MaximumDirectoryItems Then Return
                Select Case match.Groups(2).Value
                    Case "Semantic" : semantics(index) = info.Value
                    Case "Length" : lengths(index) = info.Value
                    Case "Padding" : paddings(index) = info.Value
                End Select
            Next

            Dim motionIndex = -1
            For Each pair In semantics
                If String.Equals(If(pair.Value, "").Trim(), MotionSemantic, StringComparison.Ordinal) AndAlso
                   (motionIndex < 0 OrElse pair.Key < motionIndex) Then motionIndex = pair.Key
            Next
            If motionIndex < 0 Then Return
            Dim lastIndex = semantics.Keys.Concat(lengths.Keys).Max()

            Dim videoLength = ParseLength(If(lengths.ContainsKey(motionIndex), lengths(motionIndex), Nothing))
            If videoLength < 0 Then Return
            Dim tail As Long = 0
            For item = motionIndex To lastIndex
                Dim length = ParseLength(If(lengths.ContainsKey(item), lengths(item), Nothing))
                ' Eine fehlende Polsterung heisst 0, eine unlesbare macht die Angabe unbrauchbar.
                Dim padding = If(paddings.ContainsKey(item), ParseLength(paddings(item)), 0L)
                If length < 0 OrElse padding < 0 Then Return
                ' Vor dem Addieren pruefen: die Summe darf den Bereich eines Long nicht verlassen.
                If length > Long.MaxValue - tail Then Return
                tail += length
                If padding > Long.MaxValue - tail Then Return
                tail += padding
            Next
            claim.DirectoryVideoLength = videoLength
            claim.DirectoryTailLength = tail
        End Sub

        ' ── Schritte 2 bis 4: Versatz, Plausibilitaet, Blick in die Datei ─────────

        ''' <summary>Prueft die Behauptung an der Datei. Der Strom muss sich versetzen lassen; seine
        ''' Lage ist danach unbestimmt. Gelesen werden genau zwoelf Bytes.
        '''
        ''' Zuerst gilt das Verzeichnis, dann die aeltere Angabe: eine Datei darf beide tragen, und
        ''' stimmt nur eine, ist der Film trotzdem da.</summary>
        Public Shared Function Evaluate(claim As MotionPhotoClaim, stream As Stream) As MotionPhotoInfo
            Dim result As New MotionPhotoInfo()
            If claim Is Nothing OrElse Not claim.Claimed Then Return result
            result.State = MotionPhotoState.ClaimedButMissing
            Try
                If stream Is Nothing OrElse Not stream.CanSeek OrElse Not stream.CanRead Then Return result
                Dim fileLength = stream.Length
                Dim found = TryLocate(stream, fileLength, claim.DirectoryTailLength, claim.DirectoryVideoLength)
                If Not found.HasVideo Then found = TryLocate(stream, fileLength, claim.MicroVideoOffset, claim.MicroVideoOffset)
                If found.HasVideo Then Return found
            Catch ex As Exception
                ' Lesefehler mitten in der Pruefung: behauptet, aber nicht belegt.
                DiagnosticLogService.LogException("MotionPhoto.Evaluate", ex)
            End Try
            Return result
        End Function

        ''' <summary>Ein Versuch: liegt <paramref name="tailLength"/> Bytes vor dem Dateiende ein
        ''' Film von <paramref name="videoLength"/> Bytes? Jede Angabe wird einzeln gegen die
        ''' Dateilaenge geprueft, addiert wird nichts, was ueberlaufen koennte.</summary>
        Private Shared Function TryLocate(stream As Stream, fileLength As Long, tailLength As Long, videoLength As Long) As MotionPhotoInfo
            Dim result As New MotionPhotoInfo()
            If tailLength < 0 OrElse videoLength < 0 Then Return result
            If videoLength < MinimumVideoLength OrElse videoLength > tailLength Then Return result
            If tailLength > fileLength Then Return result
            Dim offset = fileLength - tailLength
            If offset < MinimumImageLength Then Return result
            ' offset + videoLength <= fileLength folgt aus videoLength <= tailLength.

            ' Der Anfang eines MP4: vier Bytes Kastengroesse, dann der Typ "ftyp".
            Dim head(11) As Byte
            stream.Seek(offset, SeekOrigin.Begin)
            Dim read = 0
            While read < head.Length
                Dim got = stream.Read(head, read, head.Length - read)
                If got <= 0 Then Return result
                read += got
            End While
            Dim boxSize = (CLng(head(0)) << 24) Or (CLng(head(1)) << 16) Or (CLng(head(2)) << 8) Or CLng(head(3))
            If boxSize < 8 OrElse boxSize > videoLength Then Return result
            If head(4) <> AscW("f"c) OrElse head(5) <> AscW("t"c) OrElse head(6) <> AscW("y"c) OrElse head(7) <> AscW("p"c) Then Return result

            result.State = MotionPhotoState.Present
            result.VideoOffset = offset
            result.VideoLength = videoLength
            Return result
        End Function

        ''' <summary>Die ganze Erkennung fuer eine Datei: XMP aus dem Kopf des JPEG lesen (derselbe
        ''' Leser wie im EXIF-Lauf, hier nur fuer das XMP), dann pruefen. Alles, was kein lesbares
        ''' JPEG mit dieser Angabe ist, ergibt <see cref="MotionPhotoState.None"/>; geworfen wird
        ''' nie.</summary>
        Public Shared Function Detect(path As String) As MotionPhotoInfo
            Dim nothingFound As New MotionPhotoInfo()
            If String.IsNullOrWhiteSpace(path) Then Return nothingFound
            Try
                If Not File.Exists(path) Then Return nothingFound
                Using stream As New FileStream(path, FileMode.Open, FileAccess.Read,
                                               FileShare.ReadWrite Or FileShare.Delete, 4096)
                    Return Detect(stream)
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("MotionPhoto.Detect", ex)
                Return nothingFound
            End Try
        End Function

        ''' <summary>Wie <see cref="Detect(String)"/>, auf einem Strom, der sich versetzen laesst.</summary>
        Public Shared Function Detect(stream As Stream) As MotionPhotoInfo
            Dim nothingFound As New MotionPhotoInfo()
            Try
                If stream Is Nothing OrElse Not stream.CanSeek OrElse Not stream.CanRead Then Return nothingFound
                If stream.Length < MinimumImageLength Then Return nothingFound
                stream.Seek(0, SeekOrigin.Begin)
                ' Nur JPEG: der Leser unten wuerfe bei allem anderen.
                If stream.ReadByte() <> &HFF OrElse stream.ReadByte() <> &HD8 Then Return nothingFound
                stream.Seek(0, SeekOrigin.Begin)
                ' Der Leser geht die Segmente bis zum Bilddatenstrom durch und behaelt nur die mit
                ' XMP; die Bilddaten und alles dahinter fasst er nicht an.
                Dim directories = JpegMetadataReader.ReadMetadata(stream, New IJpegSegmentMetadataReader() {New XmpReader()})
                Dim claim = ReadClaim(directories)
                If Not claim.Claimed Then Return nothingFound
                Return Evaluate(claim, stream)
            Catch ex As Exception
                ' Ein Kopf, der sich nicht zerlegen laesst: keine Aussage, also kein Motion Photo.
                DiagnosticLogService.LogException("MotionPhoto.Detect", ex)
                Return nothingFound
            End Try
        End Function

        ' ── Die Frage der Speicherwege ────────────────────────────────────────────

        Private Structure CachedAnswer
            Public Length As Long
            Public WriteTicks As Long
            Public Info As MotionPhotoInfo
        End Structure

        ' Die Speicherwege fragen oft und fuer dieselbe Datei (der Knopf "Speichern" bei jeder
        ' Aenderung, ein Stapel je Kachel mehrfach). Gemerkt wird je Pfad mit Laenge und
        ' Aenderungszeit: nach jedem Schreiben stimmt der Stand nicht mehr, und es wird neu gelesen.
        Private Shared ReadOnly _answers As New ConcurrentDictionary(Of String, CachedAnswer)(StringComparer.Ordinal)
        Private Const MaximumAnswers As Integer = 512

        ''' <summary>Haengt an dieser Datei ein Film, der beim Neukodieren verloren ginge? Die Frage
        ''' von SavesRecipeBeside, CanReplaceOriginal und SaveImage. Nur fuer JPEG nach der Endung
        ''' (nur sie werden an Ort und Stelle neu kodiert und koennen einen Film tragen), damit
        ''' nicht jede RAW und jedes PNG dafuer geoeffnet wird.
        '''
        ''' WO DER KATALOG DIE ANTWORT KENNT, WIRD DIE DATEI NICHT GEOEFFNET. Der EXIF-Lauf hat die
        ''' Art schon bestimmt (Spalte MotionKind), und die Zeile gilt, solange die Datei seither
        ''' nicht geaendert wurde (LibraryService.TryGetCurrentMotion). Bei "Anpassungen anwenden"
        ''' und beim Einfuegen laeuft die Frage je gewaehltem Bild auf dem Anzeigefaden; auf einer
        ''' Netzfreigabe war das je Bild ein Oeffnen. Ohne passende Zeile gilt wie bisher die Datei.
        ''' Die letzte Sperre vor dem Ueberschreiben fragt immer die Datei
        ''' (<see cref="HasVideoInFile"/>).</summary>
        Public Shared Function HasVideo(path As String) As Boolean
            Dim remembered As MotionPhotoInfo
            If TryGetRemembered(path, remembered) Then Return remembered.HasVideo
            Dim known = CatalogAnswer(path)
            If known.HasValue Then Return known.Value
            Return DetectCached(path).HasVideo
        End Function

        ''' <summary>Dieselbe Frage, immer an der Datei selbst beantwortet (gemerkt je Pfad mit
        ''' Laenge und Aenderungszeit), ohne den Katalog. Fuer die letzte Sperre in SaveImage: dort
        ''' kostet das eine Datei je Speichern, und ein Irrtum waere ein verlorener Film.</summary>
        Public Shared Function HasVideoInFile(path As String) As Boolean
            Return DetectCached(path).HasVideo
        End Function

        ''' <summary>Fuehrt der Katalog eine Datei, die einen Film traegt, mit einer zur Datei
        ''' passenden Zeile als etwas anderes, wird die Zeile aus der Datei neu gelesen. Nur
        ''' aufrufen, wenn die Datei den Film belegt hat (HasVideoInFile); liest die Datei und
        ''' schreibt in den Katalog: im Hintergrund. Wirft nie.</summary>
        Friend Shared Sub CorrectCatalogIfWrong(path As String)
            Try
                Dim kind As MotionKind
                Dim ignoredId As String = Nothing
                If Not LibraryService.Instance.TryGetCurrentMotion(path, kind, ignoredId) OrElse kind = MotionKind.MotionPhoto Then Return
                DiagnosticLogService.LogAlways("MotionPhoto.CatalogCorrected", $"Katalog fuehrte {IO.Path.GetFileName(path)} als {kind}, die Datei traegt einen Film")
                ExifService.Invalidate(path)
                Dim data = ExifService.ReadExif(path)
                Dim fields = ExifService.ExtractSearchFields(data, path)
                LibraryService.Instance.SyncExifData(path, fields, ExifService.BuildCatalogSummary(data, fields))
            Catch ex As Exception
                DiagnosticLogService.LogException("MotionPhoto.CorrectCatalog", ex)
            End Try
        End Sub

        ''' <summary>Wo die Datei selbst zuletzt gefragt wurde und seither unveraendert ist, gilt
        ''' diese Antwort vor dem Katalog.</summary>
        Private Shared Function TryGetRemembered(path As String, ByRef info As MotionPhotoInfo) As Boolean
            info = New MotionPhotoInfo()
            If String.IsNullOrWhiteSpace(path) Then Return False
            Try
                Dim cached As CachedAnswer
                If Not _answers.TryGetValue(path, cached) Then Return False
                Dim file As New FileInfo(path)
                If Not file.Exists OrElse cached.Length <> file.Length OrElse cached.WriteTicks <> file.LastWriteTimeUtc.Ticks Then Return False
                info = cached.Info
                Return True
            Catch
                Return False
            End Try
        End Function

        ''' <summary>Fuer die Pruefungen: zaehlt, wie oft eine Datei fuer die Frage wirklich
        ''' geoeffnet wurde.</summary>
        Friend Shared FileDetections As Integer

        ''' <summary>Fuer die Pruefungen: vergisst die je Pfad gemerkten Antworten.</summary>
        Friend Shared Sub ForgetRememberedAnswers()
            _answers.Clear()
        End Sub

        ''' <summary>Was der Katalog ueber einen angehaengten Film weiss, oder Nothing, wenn er keine
        ''' Zeile hat, die zur Datei auf der Platte passt. Nur fuer JPEG nach der Endung, wie die
        ''' Frage an die Datei auch.</summary>
        Private Shared Function CatalogAnswer(path As String) As Boolean?
            If String.IsNullOrWhiteSpace(path) Then Return False
            Try
                If Not JpegExtensions.Contains(IO.Path.GetExtension(path).ToLowerInvariant()) Then Return False
                Dim kind As MotionKind
                Dim ignoredId As String = Nothing
                If LibraryService.Instance.TryGetCurrentMotion(path, kind, ignoredId) Then Return kind = MotionKind.MotionPhoto
            Catch ex As Exception
                ' Katalog nicht erreichbar: dann antwortet die Datei.
                DiagnosticLogService.LogException("MotionPhoto.CatalogAnswer", ex)
            End Try
            Return Nothing
        End Function

        ''' <summary>Behauptet das XMP dieser Datei einen Film, ob er da ist oder nicht?</summary>
        Public Shared Function IsClaimed(path As String) As Boolean
            Return DetectCached(path).IsClaimed
        End Function

        Private Shared Function DetectCached(path As String) As MotionPhotoInfo
            Dim nothingFound As New MotionPhotoInfo()
            If String.IsNullOrWhiteSpace(path) Then Return nothingFound
            Try
                If Not JpegExtensions.Contains(IO.Path.GetExtension(path).ToLowerInvariant()) Then Return nothingFound
                Dim file As New FileInfo(path)
                If Not file.Exists Then Return nothingFound
                Dim length = file.Length
                Dim ticks = file.LastWriteTimeUtc.Ticks
                Dim cached As CachedAnswer
                If _answers.TryGetValue(path, cached) AndAlso cached.Length = length AndAlso cached.WriteTicks = ticks Then
                    Return cached.Info
                End If
                Threading.Interlocked.Increment(FileDetections)
                Dim info = Detect(path)
                If _answers.Count >= MaximumAnswers Then _answers.Clear()
                _answers(path) = New CachedAnswer With {.Length = length, .WriteTicks = ticks, .Info = info}
                Return info
            Catch ex As Exception
                DiagnosticLogService.LogException("MotionPhoto.DetectCached", ex)
                Return nothingFound
            End Try
        End Function

        ' ── Abspielen: die Adresse fuer den Spieler und der Rueckfall in den Cache ──

        ''' <summary>Die Adresse, unter der der Spieler den angehaengten Film direkt aus der Datei
        ''' liest: der Ausschnitt von <see cref="MotionPhotoInfo.VideoOffset"/> bis zu seinem Ende.
        ''' Schluessel und Wert trennt das Zeichen hinter dem Namen des Protokolls (hier das
        ''' Komma), kein Gleichheitszeichen; zwei Trennzeichen beenden die Angaben, dahinter steht
        ''' nach dem Doppelpunkt der Pfad, unveraendert, auch mit Komma, Leerzeichen oder Umlaut.
        ''' Ohne den Vorsatz hielte der Spieler die Adresse fuer einen Dateinamen. Nothing, wenn
        ''' kein Film da ist. Versatz und Ende gelten nur fuer die Datei, wie sie jetzt daliegt:
        ''' unmittelbar vor dem Abspielen bestimmen, nie aufheben.</summary>
        Public Shared Function SubfileAddress(path As String, info As MotionPhotoInfo) As String
            If String.IsNullOrEmpty(path) OrElse Not info.HasVideo Then Return Nothing
            Dim videoEnd = info.VideoOffset + info.VideoLength
            Return "lavf://subfile,,start," & info.VideoOffset.ToString(CultureInfo.InvariantCulture) &
                   ",end," & videoEnd.ToString(CultureInfo.InvariantCulture) & ",,:" & path
        End Function

        ''' <summary>Der Ordner fuer den herausgeschriebenen Film: im Cache der Anwendung, nie
        ''' neben dem Foto.</summary>
        Friend Shared ReadOnly Property ClipCacheFolder As String
            Get
                Return IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                       "FerrumPix", "MotionClipCache")
            End Get
        End Property

        Private Shared ReadOnly _clipCacheLock As New Object()

        ''' <summary>Raeumt den herausgeschriebenen Film weg: beim Bildwechsel, nach dem Abspielen
        ''' und beim Start der Anwendung (dort liegt hoechstens der Rest einer Sitzung, die nicht
        ''' ordentlich endete). Liefert, wie viele Dateien gingen. Wirft nie; was sich gerade nicht
        ''' loeschen laesst (der Spieler haelt es noch), nimmt der naechste Aufruf.</summary>
        Public Shared Function ClearClipCache() As Integer
            Dim removed = 0
            SyncLock _clipCacheLock
                Try
                    Dim folder = ClipCacheFolder
                    If Not Directory.Exists(folder) Then Return 0
                    For Each file In Directory.GetFiles(folder)
                        Try
                            IO.File.Delete(file)
                            removed += 1
                        Catch ex As Exception
                            DiagnosticLogService.LogException("MotionPhoto.ClearClipCache", ex)
                        End Try
                    Next
                Catch ex As Exception
                    DiagnosticLogService.LogException("MotionPhoto.ClearClipCache", ex)
                End Try
            End SyncLock
            Return removed
        End Function

        ''' <summary>Der Rueckfall, wenn der Spieler den Film nicht direkt aus der Datei lesen kann
        ''' (<see cref="SubfileAddress"/>): schreibt den Filmteil als eigene Datei in den Cache der
        ''' Anwendung und liefert ihren Pfad, oder Nothing. EINE Datei zur Zeit: was vorher dort
        ''' lag, geht zuerst. Die Lage des Films wird hier neu bestimmt, nicht uebernommen.
        ''' Kopiert wird in Stuecken; die Laenge ist an der Datei geprueft (Detect), ein Feld in
        ''' der Groesse des Films entsteht nicht. Liest und schreibt: im Hintergrund rufen.</summary>
        Public Shared Function ExtractClip(path As String) As String
            If String.IsNullOrWhiteSpace(path) Then Return Nothing
            SyncLock _clipCacheLock
                Try
                    Dim folder = ClipCacheFolder
                    Directory.CreateDirectory(folder)
                    For Each old In Directory.GetFiles(folder)
                        Try
                            IO.File.Delete(old)
                        Catch ex As Exception
                            DiagnosticLogService.LogException("MotionPhoto.ExtractClip.Clear", ex)
                        End Try
                    Next
                    Dim target = IO.Path.Combine(folder, "clip-" & Guid.NewGuid().ToString("N") & ".mp4")
                    Using source As New FileStream(path, FileMode.Open, FileAccess.Read,
                                                   FileShare.ReadWrite Or FileShare.Delete, 81920)
                        Dim info = Detect(source)
                        If Not info.HasVideo Then Return Nothing
                        source.Seek(info.VideoOffset, SeekOrigin.Begin)
                        Dim remaining = info.VideoLength
                        Dim buffer(81919) As Byte
                        Using output As New FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.Read)
                            While remaining > 0
                                Dim got = source.Read(buffer, 0, CInt(Math.Min(CLng(buffer.Length), remaining)))
                                If got <= 0 Then Exit While
                                output.Write(buffer, 0, got)
                                remaining -= got
                            End While
                        End Using
                        If remaining > 0 Then
                            ' Die Datei wurde waehrenddessen kuerzer: ein halber Film ist keiner.
                            IO.File.Delete(target)
                            Return Nothing
                        End If
                    End Using
                    Return target
                Catch ex As Exception
                    DiagnosticLogService.LogException("MotionPhoto.ExtractClip", ex)
                    Return Nothing
                End Try
            End SyncLock
        End Function

        ' ── Neu geschriebene Dateien sind ehrlich ─────────────────────────────────

        Private Shared ReadOnly RegexTimeout As TimeSpan = TimeSpan.FromSeconds(2)

        ''' <summary>Das XMP ohne die Felder, die einen angehaengten Film behaupten. Unveraendert
        ''' zurueck, wenn nichts zu entfernen war.
        '''
        ''' Fuer jede Datei, deren Bild neu kodiert wird (Speichern unter, Export, Stapel, Collage
        ''' und das Speichern ueber eine Datei, die den Film schon nicht mehr hat): der Film reist
        ''' dabei nie mit, also darf das XMP ihn nicht ankuendigen. Entfernt statt auf 0 gesetzt,
        ''' aus demselben Grund wie bei den Farbfeldern: ein fehlendes Feld heisst "keine Angabe".
        '''
        ''' Aus dem Verzeichnis geht NUR der Eintrag "MotionPhoto". Die uebrigen (das Bild selbst,
        ''' eine Gain Map) bleiben stehen: ein Verzeichnis ohne den Film ist gueltig, und was die
        ''' uebrigen Eintraege beschreiben, haengt nicht am Film. Bliebe dabei gar kein Eintrag
        ''' uebrig, faellt das leere Verzeichnis ganz weg, denn ohne den ersten Eintrag fuer das
        ''' Bild ist es keines mehr.
        '''
        ''' Gesucht wird ueber die Kuerzel, die das Paket selbst fuer die Namensraeume erklaert; ein
        ''' gleichnamiges Feld eines fremden Namensraums bleibt unberuehrt. GRENZE wie bei den
        ''' Farbfeldern: ein komprimiert abgelegtes Paket (PNG kann das) steht nicht im Klartext da
        ''' und bleibt, wie es ist.</summary>
        Public Shared Function StripMotionFields(xmpText As String) As String
            If String.IsNullOrEmpty(xmpText) Then Return xmpText
            If xmpText.IndexOf("MotionPhoto", StringComparison.Ordinal) < 0 AndAlso
               xmpText.IndexOf("MicroVideo", StringComparison.Ordinal) < 0 Then Return xmpText
            Try
                Dim cleaned = xmpText
                For Each prefix In DeclaredPrefixes(xmpText, CameraNamespace)
                    For Each fieldName In MotionFieldNames
                        Dim field = Regex.Escape(prefix & ":" & fieldName)
                        ' Als Attribut, in beiden Anfuehrungsarten, und als eigenes Element.
                        cleaned = Regex.Replace(cleaned, "\s+" & field & "\s*=\s*""[^""]*""", "", RegexOptions.None, RegexTimeout)
                        cleaned = Regex.Replace(cleaned, "\s+" & field & "\s*=\s*'[^']*'", "", RegexOptions.None, RegexTimeout)
                        cleaned = Regex.Replace(cleaned, "\s*<" & field & "(\s[^>]*)?/>", "", RegexOptions.None, RegexTimeout)
                        cleaned = Regex.Replace(cleaned, "\s*<" & field & "(\s[^>]*)?>[^<]*</" & field & ">", "",
                                                RegexOptions.None, RegexTimeout)
                    Next
                Next

                For Each prefix In DeclaredPrefixes(xmpText, ItemNamespace)
                    Dim semantic = Regex.Escape(prefix & ":Semantic")
                    ' Ein Listenelement, in dem Semantic "MotionPhoto" steht, als Attribut oder als
                    ' Element. Der Ausdruck laeuft nie ueber das Ende eines Listenelements hinaus.
                    Dim inside = "(?:(?!</rdf:li>).)*?"
                    cleaned = Regex.Replace(cleaned,
                        "\s*<rdf:li\b" & inside & semantic & "\s*(?:=\s*""" & MotionSemantic & """|=\s*'" & MotionSemantic &
                        "'|>\s*" & MotionSemantic & "\s*</)" & inside & "</rdf:li>", "",
                        RegexOptions.Singleline, RegexTimeout)
                Next

                For Each prefix In DeclaredPrefixes(xmpText, ContainerNamespace)
                    Dim directory = Regex.Escape(prefix & ":Directory")
                    cleaned = Regex.Replace(cleaned,
                        "\s*<" & directory & "(\s[^>]*)?>\s*(?:<rdf:(Seq|Bag)(\s[^>]*)?>\s*</rdf:\2>|<rdf:(?:Seq|Bag)(\s[^>]*)?/>)\s*</" &
                        directory & ">", "", RegexOptions.Singleline, RegexTimeout)
                Next
                Return cleaned
            Catch ex As Exception
                ' Ein unerwarteter Aufbau darf das Speichern nicht kosten.
                DiagnosticLogService.LogException("MotionPhoto.StripMotionFields", ex)
                Return xmpText
            End Try
        End Function

        ''' <summary>Die Kuerzel, unter denen ein Paket einen Namensraum erklaert (meist eines).</summary>
        Private Shared Function DeclaredPrefixes(xmpText As String, ns As String) As List(Of String)
            Dim result As New List(Of String)()
            For Each match As Match In Regex.Matches(xmpText, "xmlns:([A-Za-z_][\w.\-]*)\s*=\s*[""']" & Regex.Escape(ns) & "[""']",
                                                     RegexOptions.None, RegexTimeout)
                Dim prefix = match.Groups(1).Value
                If Not result.Contains(prefix) Then result.Add(prefix)
            Next
            Return result
        End Function

    End Class

End Namespace
