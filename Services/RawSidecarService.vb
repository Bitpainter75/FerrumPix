Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Security.Cryptography
Imports System.Text
Imports System.Xml
Imports System.Xml.Linq

Namespace Services

    ''' <summary>
    ''' Rezept-Begleitdatei ("foto.cr2" -> "foto.cr2.fpxmp", "foto.psd" -> "foto.psd.fpxmp",
    ''' "foto.jpg" -> "foto.jpg.fpxmp"). Gebaut fuer die NUR LESBAREN Formate RAW und PSD; seit der
    ''' Einstellung "neben dem Bild speichern" traegt sie auch das Rezept von JPG, PNG, WebP und den
    ''' nicht ueber das Original schreibbaren Rasterformaten (TIFF, HEIC/AVIF, JPEG XL, BMP, GIF).
    ''' Welche Frage wofuer gilt, steht bei IsReadOnlyFormat, HasRecipeBeside, SavesRecipeBeside und
    ''' WritesRecipe. Katalogdaten traegt sie nur bei RAW und PSD. Am XMP-Sidecar-Konzept
    ''' orientiert (kleine XML-Datei NEBEN dem Original, wird beim Oeffnen automatisch geladen und
    ''' beim Verlassen aktualisiert), aber ein EIGENES Format - die Reglermodelle sind nicht
    ''' Adobe-kompatibel, ein echtes XMP wuerde nur so tun als ob.
    '''
    ''' Inhalt: eine XML-Huelle (Version, Quelldatei, Zeitstempel) mit dem Rezept-JSON des
    ''' .fpx-Formats als CDATA sowie Bewertung, Favorit, Farbetikett und Stichwoertern aus dem Katalog. Bewusst
    ''' KEINE eigene XML-Abbildung aller Regler: die JSON-Regeln aus FpxService sind
    ''' konstruktorbasiert erprobt (VB kann keine JsonConverter schreiben), und beide Formate
    ''' koennen so nie auseinanderdriften.
    '''
    ''' Grenzen (bewusst): gebackene Pixel-Bearbeitungen (Pinsel/Radierer/Retusche/gerasterte
    ''' Ebenen) stecken im ARBEITSBILD, nicht im Rezept. Auch Objekte gehoeren in eine .fpx:
    ''' externe Bild-Assets wuerden im Sidecar nur ueber absolute Pfade referenziert und nicht wie
    ''' im .fpx-Buendel eingebettet. Der Editor bietet den Sidecar-Speicherweg deshalb nur fuer
    ''' reine Entwicklungseinstellungen an.
    '''
    ''' Der Sidecar ist NICHT abschaltbar (die Einstellung dafuer ist entfallen): fuer
    ''' RAW und PSD ist er der EINZIGE Weg, eine Bearbeitung zu behalten, ohne die Quelldatei zu
    ''' zerstoeren - beide Formate koennen wir nicht schreiben. Ein Schalter dafuer hiess in der
    ''' Praxis "Bearbeitung stillschweigend wegwerfen", und er liess Viewer und Editor
    ''' auseinanderlaufen: der Viewer las den Sidecar bedingungslos, der Editor nur bei aktiver
    ''' Einstellung, womit dieselbe Datei je nach Ansicht anders gedreht aussah.
    ''' </summary>
    Partial Public NotInheritable Class RawSidecarService

        Private Sub New()
        End Sub

        ''' <summary>Die NUR LESBAREN Formate: RAW und PSD/PSB (siehe die Schreibsperre in
        ''' ImageProcessor.SaveImage). Nur sie tragen Katalogdaten (Sterne, Etikett, Stichwoerter,
        ''' Beschreibung) in der .fpxmp, nur sie haben Varianten, und ein Serverbild dieser Formate
        ''' wird nie durch einen Render ersetzt.
        '''
        ''' NICHT die Frage, ob ein Rezept daneben liegt oder dorthin gespeichert wird: dafuer gibt
        ''' es <see cref="HasRecipeBeside"/>, <see cref="SavesRecipeBeside"/> und
        ''' <see cref="WritesRecipe"/>. Bis zur Einstellung "neben dem Bild speichern" hiess diese
        ''' Funktion IsSidecarFormat und trug beide Bedeutungen.</summary>
        Public Shared Function IsReadOnlyFormat(path As String) As Boolean
            If String.IsNullOrWhiteSpace(path) Then Return False
            ' Auch fuer den Schluessel einer Variante: gefragt ist das Format der Datei dahinter.
            Dim file = VariantKey.FileOf(path)
            Return RawPreviewService.IsSupportedRaw(file) OrElse PsdPreviewService.IsSupportedPsd(file)
        End Function

        ''' <summary>Rasterformate, die neben sich ein Rezept tragen koennen. Ausgenommen bleiben
        ''' .fpx (traegt sein Rezept selbst), SVG, ICO samt Mauszeiger (.cur), Video und PDF. .jpe,
        ''' .jfif und .jps sind JPEG, .apng ist PNG, lassen sich aber nicht an Ort und Stelle schreiben
        ''' (CanEncodeToTargetExtension); ihr Speichern geht deshalb wie bei TIFF immer ins Rezept,
        ''' ebenso bei .wbmp, fuer das es keinen Encoder gibt, und bei JPEG 2000. EXR, Radiance HDR
        ''' und TIFF mit Gleitkomma stehen nicht hier: sie gehen den RAW-Weg (IsReadOnlyFormat).</summary>
        Private Shared ReadOnly RecipeRasterExtensions As String() = {
            ".jpg", ".jpeg", ".jpe", ".jfif", ".jps", ".png", ".apng", ".webp", ".tif", ".tiff",
            ".heic", ".heif", ".hif", ".avif", ".jxl", ".bmp", ".wbmp", ".gif",
            ".jp2", ".j2k", ".jph", ".j2c", ".jpc", ".jpf", ".jhc"}

        ''' <summary>Kann diese Datei ein Rezept in einer .fpxmp neben sich haben? RAW, PSD und die
        ''' Rasterformate aus <see cref="RecipeRasterExtensions"/>.</summary>
        Public Shared Function IsRecipeFormat(path As String) As Boolean
            If String.IsNullOrWhiteSpace(path) Then Return False
            If IsReadOnlyFormat(path) Then Return True
            Dim file = VariantKey.FileOf(path)
            Return RecipeRasterExtensions.Contains(System.IO.Path.GetExtension(file).ToLowerInvariant())
        End Function

        ''' <summary>Liegt neben dieser Datei ein Rezept, das beim Lesen eingerechnet wird? GILT FUER
        ''' JEDES REZEPTFORMAT UND UNABHAENGIG VON DER EINSTELLUNG: wer von "neben dem Bild" auf "in
        ''' die Datei" umstellt, sieht seine Bearbeitungen weiter. Nur so bleibt das Umlegen folgenlos.
        ''' Fragen danach Editor (Oeffnen, Blaettern), Betrachter, Kachel, Ausgabe, Stapel, Kopieren
        ''' und der Vermerk "bearbeitet".</summary>
        Public Shared Function HasRecipeBeside(path As String) As Boolean
            Return IsRecipeFormat(path) AndAlso Exists(path)
        End Function

        ''' <summary>Geht "Speichern" im Editor fuer diese Datei ins Rezept daneben statt in die Datei?
        ''' Ja fuer RAW und PSD, fuer jedes Rasterformat, das sich nicht ueber das Original schreiben
        ''' laesst (TIFF, HEIC/AVIF, JPEG XL, BMP, GIF), und fuer JPG, PNG und WebP mit der
        ''' Einstellung "neben dem Bild speichern" (AppSettings.SaveEditsBesideOriginal).
        '''
        ''' Arbeitskopien vom Server bleiben dabei, wie sie waren (nur RAW und PSD): ihre Begleitdatei
        ''' laege im Temp-Ordner und waere beim naechsten Aufraeumen fort. Den Weg zum Server kennt
        ''' fuer andere Formate noch niemand.</summary>
        Public Shared Function SavesRecipeBeside(path As String) As Boolean
            If Not IsRecipeFormat(path) Then Return False
            If IsReadOnlyFormat(path) Then Return True
            Dim file = VariantKey.FileOf(path)
            If ImmichService.IsImmichTempPath(file) OrElse NextcloudService.IsNextcloudTempPath(file) Then Return False
            If Not ImageProcessor.CanEncodeToTargetExtension(file) Then Return True
            Return AppSettingsService.Load().SaveEditsBesideOriginal
        End Function

        ''' <summary>Die Frage der "leichten" Schreiber, die eine Bearbeitung auf eine Datei legen,
        ''' ohne sie zu oeffnen (Drehen im Betrachter, Anpassungen einfuegen, "Anpassungen anwenden"
        ''' mit Ueberschreiben, die Rahmen-Uebertragung): ins Rezept, wenn "Speichern" dorthin geht
        ''' ODER schon eines daneben liegt. Ein JPG mit Rezept bekommt also auch bei "in die Datei"
        ''' keine Drehung in die Pixel, deren Zuschnitt im Rezept dann verrutscht waere.</summary>
        Public Shared Function WritesRecipe(path As String) As Boolean
            Return SavesRecipeBeside(path) OrElse HasRecipeBeside(path)
        End Function

        ''' <summary>Entfernt das Rezept einer Datei, deren Bildpunkte gerade MIT diesem Rezept neu
        ''' geschrieben wurden (Speichern in die Datei, Stapel mit Ueberschreiben). Sonst laege
        ''' derselbe Zuschnitt auf den Pixeln und noch einmal im Rezept. Nur fuer Formate, die keine
        ''' Katalogdaten in der .fpxmp tragen: dort steht ausser Rezept und Vermerken nichts darin,
        ''' die ganze Datei geht also weg, der Vermerk "bearbeitet" mit ihr. RAW und PSD werden nie
        ''' neu geschrieben und behalten ihre Begleitdatei immer (False). Fehlt sie, ist das kein
        ''' Fehler (True).</summary>
        Public Shared Function TryRemoveRecipe(path As String) As Boolean
            If String.IsNullOrWhiteSpace(path) OrElse IsReadOnlyFormat(path) Then Return False
            Dim sidecar = SidecarPathFor(path)
            SyncLock _writeLock
                Try
                    If File.Exists(sidecar) Then File.Delete(sidecar)
                    ForgetDisplayFacts(sidecar)
                    Return True
                Catch ex As Exception
                    DiagnosticLogService.LogException("Sidecar.RemoveRecipe", ex)
                    Return False
                End Try
            End SyncLock
        End Function

        Public Const Extension As String = ".fpxmp"
        Private Const FormatVersion As Integer = 2
        ''' <summary>Die Nummer einer Datei, die mehr als eine Variante traegt (siehe
        ''' RawSidecarServiceVariants.vb). Eine Datei mit nur einer bleibt bei
        ''' <see cref="FormatVersion"/> und damit fuer aeltere Fassungen lesbar; eine mit mehreren
        ''' gilt dort als unlesbar und wird deshalb nicht angefasst. Sonst schriebe eine aeltere
        ''' Fassung sie beim naechsten Stern ohne die geparkten Varianten zurueck.</summary>
        Private Const VariantsFormatVersion As Integer = 3
        Private Shared ReadOnly Ns As XNamespace = "https://github.com/Bitpainter75/FerrumPix/ns/recipe/1.0"
        Private Shared ReadOnly _writeLock As New Object()

        ''' <summary>Katalogteil einer .fpxmp. Nullable Werte unterscheiden alte Version-1-Dateien
        ''' ohne Katalogblock von einem bewusst gesetzten 0/False; HasKeywords tut dasselbe fuer eine
        ''' bewusst geleerte Stichwortliste.</summary>
        Public NotInheritable Class RawSidecarCatalogData
            Public Property Rating As Integer?
            Public Property IsFavorite As Boolean?
            ''' Nothing = Feld fehlt (alte Sidecar), Leerstring = Etikett bewusst entfernt.
            Public Property ColorLabel As String = Nothing
            Public Property Keywords As New List(Of String)()
            Public Property HasKeywords As Boolean
            ''' Nothing = Feld fehlt (aeltere Sidecar); sonst -1 verworfen, 0 keine, 1 behalten.
            Public Property PickState As Integer?
        End Class

        ''' <summary>"foto.cr2" -> "foto.cr2.fpxmp" (voller Name bleibt erhalten, damit
        ''' foto.cr2 und foto.dng nie um denselben Sidecar konkurrieren). Der Schluessel einer
        ''' Variante fuehrt zur Begleitdatei ihrer Datei: alle Varianten teilen sich die eine.</summary>
        Public Shared Function SidecarPathFor(rawPath As String) As String
            Return VariantKey.FileOf(rawPath) & Extension
        End Function

        Public Shared Function Exists(rawPath As String) As Boolean
            Return Not String.IsNullOrWhiteSpace(rawPath) AndAlso File.Exists(SidecarPathFor(rawPath))
        End Function

        ''' <summary>Schreibt das Rezept neben die RAW-Datei (atomar via Temp+Move). False bei Fehler -
        ''' der Aufrufer behaelt dann sein normales "ungespeicherte Aenderungen"-Verhalten.</summary>
        ''' <param name="developedThumbnail">Der Vermerk, dass die Kachel dieser Datei entwickelt
        ''' gezeigt wird, auch wenn die Einstellung dafuer aus ist (siehe
        ''' <see cref="ReadDevelopedThumbnail"/>). Nothing laesst einen vorhandenen Vermerk stehen:
        ''' der Editor und jeder andere Schreiber sollen ihn nicht still verlieren.</param>
        ''' <param name="requireAbsent">Nur anlegen, wenn am Ziel noch keine Datei liegt. Fuer Aufrufer,
        ''' die vorher "fehlt" gelesen haben: entsteht die Datei dazwischen, wird sie nicht ersetzt,
        ''' und das Ergebnis ist False.</param>
        ''' <param name="edited">Der Vermerk "bearbeitet" (siehe <see cref="IsEditedFile"/>). Gesetzt
        ''' wird er dort, wo bearbeitet wird; Nothing laesst einen vorhandenen stehen, wie beim
        ''' Vermerk der entwickelten Kachel.</param>
        Public Shared Function TryWrite(rawPath As String, adjustments As ImageAdjustments,
                                        Optional developedThumbnail As Boolean? = Nothing,
                                        Optional requireAbsent As Boolean = False,
                                        Optional edited As Boolean? = Nothing) As Boolean
            Return WriteChecked(rawPath, adjustments, Nothing, developedThumbnail, edited, requireAbsent).Succeeded
        End Function

        ' ── Stand beim Lesen, Vergleich vor dem Schreiben ─────────────────────────
        ' Wer eine Begleitdatei liest, lange damit arbeitet und dann schreibt (der Editor), merkt
        ' sich ihren Stand (SidecarStamp). Vor dem Schreiben wird er mit der Platte verglichen,
        ' immer ueber die Bytes, nie nur ueber Zeit und Laenge: grob aufloesende Dateisysteme (FAT,
        ' SMB) tragen nach zwei Schreibvorgaengen dieselbe Zeit, und eine geaenderte Bewertung
        ' aendert die Laenge nicht. Ist die Datei inzwischen anders, gibt es vier Ausgaenge
        ' (SidecarWriteOutcome); keiner verliert eine Bearbeitung still.
        '
        ' Zwischen Prozessen dieser Anwendung haelt eine benannte Sperre je Begleitdatei Lesen und
        ' Schreiben zusammen (LockAcrossProcesses). Gegen andere Programme und gegen einen zweiten
        ' Rechner auf derselben Freigabe hilft sie nicht: dort vergleicht der Schreiber unmittelbar
        ' vor dem Umbenennen noch einmal die Bytes mit dem Stand, auf dem seine Entscheidung beruht,
        ' und faengt bei einer Abweichung von vorn an (hoechstens MaxWriteAttempts mal). Das Fenster
        ' zwischen diesem Vergleich und dem Umbenennen schliesst das nicht ganz.

        ''' <summary>Der Stand einer Begleitdatei, wie ein Leser ihn gesehen hat: ob sie da war, ein
        ''' SHA-256 ihrer Bytes, das Rezept des gemeinten Schluessels in vergleichbarer Form und ihr
        ''' Katalogblock. Der Editor holt ihn beim Oeffnen (<see cref="ReadStamp"/>) und reicht ihn
        ''' beim Speichern an <see cref="TryWriteChecked"/>.</summary>
        Public NotInheritable Class SidecarStamp
            ''' Der Schluessel, fuer den der Stand gilt: die Datei oder eine ihrer Varianten.
            Public ReadOnly Property ItemPath As String
            Public ReadOnly Property Exists As Boolean
            ''' SHA-256 der Bytes; Nothing ohne Datei oder wenn sie sich nicht lesen liess.
            Public ReadOnly Property ContentHash As String
            ''' Das Rezept als JSON nach dem Lesen und erneuten Schreiben. Nothing heisst: kein Rezept
            ''' (keine Datei, oder nur Katalogdaten mit neutralem Rezept und ohne Vermerk
            ''' "bearbeitet"). <see cref="UnknownRecipe"/> heisst: nicht verstanden.
            Friend ReadOnly Property RecipeKey As String
            ''' Der Katalogblock des Schluessels; Nothing ohne Block.
            Friend ReadOnly Property Catalog As RawSidecarCatalogData
            ''' Die Bytes selbst, nur fuer das Aufheben unter dem Schreibvorgang; der Editor
            ''' bekommt einen Stand ohne sie.
            Friend ReadOnly Property Bytes As Byte()

            Friend Sub New(itemPath As String, exists As Boolean, contentHash As String, recipeKey As String,
                           catalog As RawSidecarCatalogData, bytes As Byte())
                Me.ItemPath = itemPath
                Me.Exists = exists
                Me.ContentHash = contentHash
                Me.RecipeKey = recipeKey
                Me.Catalog = catalog
                Me.Bytes = bytes
            End Sub

            ''' <summary>Derselbe Stand ohne die Bytes.</summary>
            Friend Function WithoutBytes() As SidecarStamp
                Return New SidecarStamp(ItemPath, Exists, ContentHash, RecipeKey, Catalog, Nothing)
            End Function

            ''' <summary>Liegt auf der Platte noch genau dieser Inhalt? Ein Stand mit unbekanntem
            ''' Inhalt gleicht nie.</summary>
            Friend Function SameContentAs(other As SidecarStamp) As Boolean
                If other Is Nothing OrElse Exists <> other.Exists Then Return False
                If Not Exists Then Return True
                Return ContentHash IsNot Nothing AndAlso String.Equals(ContentHash, other.ContentHash, StringComparison.Ordinal)
            End Function
        End Class

        ''' <summary>Steht fuer ein Rezept, das sich nicht lesen liess. Gleicht keinem anderen und
        ''' keinem JSON, ein Vergleich damit faellt also immer auf die sichere Seite.</summary>
        Friend Const UnknownRecipe As String = "?unbekannt"

        ''' <summary>Was <see cref="TryWriteChecked"/> getan hat.</summary>
        Public Enum SidecarWriteOutcome
            ''' Die Datei war, wie sie gelesen wurde; der neue Stand ist geschrieben.
            Written
            ''' Ein anderer hat nur Katalog, Beschreibung, Varianten oder Vermerke geaendert, oder
            ''' dasselbe Rezept geschrieben: unser Rezept mit seinen uebrigen Teilen ist geschrieben.
            Merged
            ''' Am Rezept war seit dem Lesen nichts geaendert, ein anderer hat es aber neu
            ''' geschrieben: seine Fassung bleibt, geschrieben wird nichts.
            Adopted
            ''' Beide haben das Rezept geaendert: die Fassung der Platte liegt daneben als
            ''' <see cref="SidecarWriteResult.PreservedPath"/>, unsere ist geschrieben.
            Preserved
            Failed
        End Enum

        Public NotInheritable Class SidecarWriteResult
            Public Property Outcome As SidecarWriteOutcome = SidecarWriteOutcome.Failed
            ''' Bei Preserved die aufgehobene fremde Fassung.
            Public Property PreservedPath As String
            ''' Der Stand fuer das naechste Speichern. Nach Adopted bleibt es der alte: der Leser
            ''' zeigt weiter seinen Stand, und speichert er spaeter eine Aenderung daran, muss die
            ''' fremde Fassung aufgehoben werden statt ersetzt.
            Public Property Stamp As SidecarStamp

            Public ReadOnly Property Succeeded As Boolean
                Get
                    Return Outcome <> SidecarWriteOutcome.Failed
                End Get
            End Property
        End Class

        ''' <summary>So oft wird neu entschieden, wenn ein anderes Programm zwischen Pruefung und
        ''' Umbenennen schreibt. Danach gilt das Schreiben als gescheitert.</summary>
        Private Const MaxWriteAttempts As Integer = 3

        ''' <summary>Hoechstens so viele aufgehobene fremde Fassungen je Begleitdatei
        ''' (".other", ".other2" bis ".other4"). Sind alle belegt und anders, wird nicht geschrieben.</summary>
        Public Const MaxOtherVersions As Integer = 4
        Public Const OtherVersionSuffix As String = ".other"

        ''' <summary>Der Stand der Begleitdatei zu diesem Schluessel (Datei oder Variante), fuer
        ''' <see cref="TryWriteChecked"/>. VOR dem Lesen des Rezepts holen: aendert ein anderer die
        ''' Datei dazwischen, ist der Stand der aeltere, und das Speichern hebt die fremde Fassung
        ''' im Zweifel auf, statt sie zu ersetzen.</summary>
        Public Shared Function ReadStamp(rawPath As String) As SidecarStamp
            If String.IsNullOrWhiteSpace(rawPath) Then Return Nothing
            SyncLock _writeLock
                Return CaptureStamp(rawPath).WithoutBytes()
            End SyncLock
        End Function

        ''' <summary>Ein Stand, der nichts weiss: fuer einen Leser, der den Stand nicht holen konnte.
        ''' Jede vorhandene Datei gilt dann als fremd geaendert, und ihr Rezept wird aufgehoben, bevor
        ''' ein anderes sie ersetzt.</summary>
        Public Shared Function UnknownStamp(rawPath As String) As SidecarStamp
            Return New SidecarStamp(rawPath, True, Nothing, UnknownRecipe, Nothing, Nothing)
        End Function

        ''' <summary>Die Pfade der aufgehobenen fremden Fassungen dieser Datei, ob vorhanden oder
        ''' nicht: "foto.cr2.fpxmp.other", dann ".other2" bis ".other4".</summary>
        Public Shared Function OtherVersionPaths(rawPath As String) As List(Of String)
            Dim sidecar = SidecarPathFor(rawPath)
            Dim result As New List(Of String)()
            For index = 1 To MaxOtherVersions
                result.Add(sidecar & OtherVersionSuffix & If(index = 1, "", index.ToString(Globalization.CultureInfo.InvariantCulture)))
            Next
            Return result
        End Function

        ''' <summary>Schreibt das Rezept wie <see cref="TryWrite"/>, vergleicht aber vorher die
        ''' Datei mit dem Stand <paramref name="opened"/> beim Lesen. Ohne Stand (Nothing) wie
        ''' TryWrite. Siehe <see cref="SidecarWriteOutcome"/> fuer die Ausgaenge.</summary>
        Public Shared Function TryWriteChecked(rawPath As String, adjustments As ImageAdjustments,
                                               opened As SidecarStamp,
                                               Optional developedThumbnail As Boolean? = Nothing,
                                               Optional edited As Boolean? = Nothing) As SidecarWriteResult
            If opened IsNot Nothing AndAlso Not String.Equals(opened.ItemPath, rawPath, PathIdentity.Comparison) Then
                ' Der Stand eines anderen Schluessels taugt nicht als Vergleich. Unbekannt ist
                ' sicherer als keiner: dann wird eine abweichende Datei aufgehoben statt ersetzt.
                DiagnosticLogService.LogAlways("Sidecar.Write", $"Stand gehoert zu einem anderen Schluessel: {Path.GetFileName(opened.ItemPath)}")
                opened = New SidecarStamp(rawPath, True, Nothing, UnknownRecipe, Nothing, Nothing)
            End If
            Return WriteChecked(rawPath, adjustments, opened, developedThumbnail, edited, requireAbsent:=False)
        End Function

        Private Shared Function WriteChecked(rawPath As String, adjustments As ImageAdjustments, opened As SidecarStamp,
                                             developedThumbnail As Boolean?, edited As Boolean?,
                                             requireAbsent As Boolean) As SidecarWriteResult
            Dim failed = New SidecarWriteResult With {.Outcome = SidecarWriteOutcome.Failed, .Stamp = opened}
            If String.IsNullOrWhiteSpace(rawPath) OrElse adjustments Is Nothing Then Return failed
            Try
                SyncLock _writeLock
                    Using crossLock = LockAcrossProcesses(rawPath)
                        If crossLock Is Nothing Then Return failed
                        For attempt = 1 To MaxWriteAttempts
                            Dim disk As SidecarStamp = Nothing
                            Dim result = WriteCheckedOnce(rawPath, adjustments, opened, developedThumbnail, edited,
                                                          requireAbsent, disk)
                            If result IsNot Nothing Then Return result
                            ' Nothing: gescheitert. Hat sich die Datei seit der Entscheidung geaendert,
                            ' wird neu entschieden, sonst bleibt es dabei.
                            If disk Is Nothing OrElse CaptureStamp(rawPath, parse:=False).SameContentAs(disk) Then Return failed
                            DiagnosticLogService.LogAlways("Sidecar.Write",
                                $"waehrend des Schreibens von aussen geaendert, Versuch {attempt}: {Path.GetFileName(SidecarPathFor(rawPath))}")
                        Next
                        Return failed
                    End Using
                End SyncLock
            Catch ex As Exception
                DiagnosticLogService.LogException("Sidecar.Write", ex)
                Return failed
            End Try
        End Function

        ''' <summary>Ein Durchgang von <see cref="WriteChecked"/>: Platte lesen, entscheiden,
        ''' schreiben. Nothing heisst gescheitert; <paramref name="disk"/> ist dann der Stand, auf dem
        ''' die Entscheidung beruhte, oder Nothing, wenn es gar nicht so weit kam.</summary>
        Private Shared Function WriteCheckedOnce(rawPath As String, adjustments As ImageAdjustments, opened As SidecarStamp,
                                                 developedThumbnail As Boolean?, edited As Boolean?,
                                                 requireAbsent As Boolean, ByRef disk As SidecarStamp) As SidecarWriteResult
            Dim variantId As String = Nothing
            Dim file = VariantKey.Split(rawPath, variantId)
            Dim captured = CaptureStamp(rawPath)
            Dim existing As ImageAdjustments = Nothing
            Dim state = ReadRecipe(rawPath, existing)
            If state = SidecarReadResult.Unreadable Then Return Nothing
            If requireAbsent AndAlso captured.Exists Then Return Nothing
            disk = captured

            Dim outcome = SidecarWriteOutcome.Written
            Dim preserved As String = Nothing
            Dim diskChanged = opened IsNot Nothing AndAlso Not opened.SameContentAs(captured)
            If diskChanged Then
                Dim ours = NormalizedRecipe(adjustments)
                If String.Equals(captured.RecipeKey, opened.RecipeKey, StringComparison.Ordinal) OrElse
                   String.Equals(captured.RecipeKey, ours, StringComparison.Ordinal) Then
                    ' Das Rezept der Platte ist das gelesene oder schon unseres: der andere hat nur
                    ' den Rest geaendert, und der bleibt beim Schreiben stehen.
                    outcome = SidecarWriteOutcome.Merged
                ElseIf String.Equals(ours, opened.RecipeKey, StringComparison.Ordinal) Then
                    ' Wir haben am Rezept nichts geaendert, der andere schon: seine Fassung gilt.
                    DiagnosticLogService.LogAlways("Sidecar.Write", $"fremde Fassung uebernommen: {Path.GetFileName(SidecarPathFor(file))}")
                    SyncCatalogFromFile(file)
                    Return New SidecarWriteResult With {.Outcome = SidecarWriteOutcome.Adopted, .Stamp = opened}
                ElseIf captured.Exists Then
                    ' Beide haben das Rezept geaendert: die Fassung der Platte geht nicht verloren.
                    preserved = PreserveOtherVersion(SidecarPathFor(file), captured.Bytes)
                    If preserved Is Nothing Then
                        DiagnosticLogService.LogAlways("Sidecar.Write",
                            $"fremde Fassung nicht aufzuheben, nichts geschrieben: {Path.GetFileName(SidecarPathFor(file))}")
                        Return Nothing
                    End If
                    outcome = SidecarWriteOutcome.Preserved
                End If
            End If

            Dim written As Boolean
            If variantId.Length > 0 Then
                written = WriteVariantRecipe(file, variantId, adjustments, edited, guard:=captured) = ModifyRecipeResult.Written
            Else
                Dim catalog = LibraryService.Instance.GetSidecarCatalogData(rawPath)
                ' Was ein anderer seit dem Lesen am Katalog geaendert hat, gilt Feld fuer Feld vor
                ' dem Stand der eigenen Datenbank, der davon nichts weiss.
                If diskChanged Then catalog = MergeCatalog(opened.Catalog, captured.Catalog, catalog)
                written = TryWriteCore(rawPath, adjustments, catalog, developedThumbnail, edited,
                                       requireAbsent:=Not captured.Exists, guard:=captured)
            End If
            If Not written Then Return Nothing
            If diskChanged Then
                DiagnosticLogService.LogAlways("Sidecar.Write",
                    $"fremde Aenderung gefunden, Ausgang {outcome}: {Path.GetFileName(SidecarPathFor(file))}")
                If variantId.Length = 0 Then SyncCatalogFromFile(file)
            End If
            Return New SidecarWriteResult With {
                .Outcome = outcome,
                .PreservedPath = preserved,
                .Stamp = If(opened Is Nothing, Nothing, CaptureStamp(rawPath).WithoutBytes())}
        End Function

        ''' <summary>Liest die Begleitdatei EINMAL als Bytes und leitet alles aus diesen ab.
        ''' Fehlt sie, ein Stand ohne Datei; laesst sie sich nicht lesen, einer mit unbekanntem
        ''' Inhalt. Ohne <paramref name="parse"/> nur Dasein und Hash, fuer den Vergleich vor dem
        ''' Umbenennen.</summary>
        Private Shared Function CaptureStamp(rawPath As String, Optional parse As Boolean = True) As SidecarStamp
            Dim sidecar = SidecarPathFor(rawPath)
            Select Case ProbeSidecar(sidecar)
                Case SidecarReadResult.Missing
                    Return New SidecarStamp(rawPath, False, Nothing, Nothing, Nothing, Nothing)
                Case SidecarReadResult.Unreadable
                    Return New SidecarStamp(rawPath, True, Nothing, UnknownRecipe, Nothing, Nothing)
            End Select
            Dim bytes As Byte()
            Try
                bytes = File.ReadAllBytes(sidecar)
            Catch ex As FileNotFoundException
                Return New SidecarStamp(rawPath, False, Nothing, Nothing, Nothing, Nothing)
            Catch ex As DirectoryNotFoundException
                Return New SidecarStamp(rawPath, False, Nothing, Nothing, Nothing, Nothing)
            Catch
                Return New SidecarStamp(rawPath, True, Nothing, UnknownRecipe, Nothing, Nothing)
            End Try
            Dim hash = Convert.ToHexString(SHA256.HashData(bytes))
            If Not parse Then Return New SidecarStamp(rawPath, True, hash, UnknownRecipe, Nothing, bytes)
            Dim recipeKey = UnknownRecipe
            Dim catalog As RawSidecarCatalogData = Nothing
            Try
                Using stream As New MemoryStream(bytes, writable:=False)
                    Dim root = XDocument.Load(stream).Root
                    recipeKey = RecipeKeyFromRoot(root, rawPath)
                    catalog = CatalogFromRoot(root, rawPath)
                End Using
            Catch
                recipeKey = UnknownRecipe
            End Try
            Return New SidecarStamp(rawPath, True, hash, recipeKey, catalog, bytes)
        End Function

        ''' <summary>Das Rezept des Schluessels in vergleichbarer Form (siehe
        ''' <see cref="SidecarStamp.RecipeKey"/>). Dieselben Regeln wie <see cref="ReadRecipeCore"/>:
        ''' was dort unlesbar ist, ist hier <see cref="UnknownRecipe"/>.</summary>
        Private Shared Function RecipeKeyFromRoot(root As XElement, rawPath As String) As String
            If root Is Nothing OrElse root.Name <> Ns + "recipe" Then Return UnknownRecipe
            Dim version As Integer
            If Not Integer.TryParse(root.Attribute("version")?.Value, Globalization.NumberStyles.Integer,
                                    Globalization.CultureInfo.InvariantCulture, version) Then Return UnknownRecipe
            If version < 1 OrElse version > VariantsFormatVersion Then Return UnknownRecipe
            Dim variants As VariantSet = Nothing
            If Not TryParseVariants(root, variants) Then Return UnknownRecipe
            Dim variantId As String = Nothing
            Dim file = VariantKey.Split(rawPath, variantId)
            Dim json As String
            If variantId.Length > 0 Then
                Dim entry = variants?.Find(variantId)
                If entry Is Nothing Then Return UnknownRecipe
                json = entry.Json
            Else
                Dim node = root.Element(Ns + "adjustments")
                If node Is Nothing Then Return UnknownRecipe
                json = node.Value
            End If
            Dim adjustments = FpxService.DeserializeAdjustments(json)
            If adjustments Is Nothing Then Return UnknownRecipe
            DenoiseModelService.MigrateLegacyRecipe(adjustments, file)
            Dim key = NormalizedRecipe(adjustments)
            ' Eine Datei, die nur Katalogdaten traegt (angelegt von einem Stern oder Stichwort, mit
            ' neutralem Rezept und ohne Vermerk "bearbeitet"), hat kein Rezept. Sonst hielte das
            ' Speichern im Editor einen Stern, der waehrenddessen vergeben wurde, fuer ein fremdes
            ' Rezept und hoebe die Datei auf.
            If variantId.Length = 0 AndAlso root.Attribute(EditedAttribute) Is Nothing AndAlso
               String.Equals(key, NormalizedRecipe(New ImageAdjustments()), StringComparison.Ordinal) Then Return Nothing
            Return key
        End Function

        ''' <summary>Ein Rezept so, wie es in die Datei ginge: gebackene Vorgaenge noch anzuwenden
        ''' (siehe TryWriteCore), als JSON.</summary>
        Private Shared Function NormalizedRecipe(adjustments As ImageAdjustments) As String
            If adjustments Is Nothing Then Return Nothing
            Dim copy = adjustments.Clone()
            copy.BakedOperationsApplied = False
            Return FpxService.SerializeAdjustments(copy)
        End Function

        ''' <summary>Der Katalog fuer das Schreiben nach einer fremden Aenderung: jedes Feld, das auf
        ''' der Platte anders ist als beim Lesen, kommt von der Platte (es ist juenger als unser
        ''' Stand), jedes andere aus der eigenen Datenbank.</summary>
        Private Shared Function MergeCatalog(opened As RawSidecarCatalogData, disk As RawSidecarCatalogData,
                                             own As RawSidecarCatalogData) As RawSidecarCatalogData
            If disk Is Nothing Then Return own
            Dim result As New RawSidecarCatalogData With {
                .Rating = own?.Rating, .IsFavorite = own?.IsFavorite, .ColorLabel = own?.ColorLabel,
                .Keywords = If(own?.Keywords, New List(Of String)()), .HasKeywords = own IsNot Nothing AndAlso own.HasKeywords,
                .PickState = own?.PickState}
            If disk.Rating.HasValue AndAlso Not Nullable.Equals(disk.Rating, opened?.Rating) Then result.Rating = disk.Rating
            If disk.IsFavorite.HasValue AndAlso Not Nullable.Equals(disk.IsFavorite, opened?.IsFavorite) Then result.IsFavorite = disk.IsFavorite
            If disk.ColorLabel IsNot Nothing AndAlso
               Not String.Equals(disk.ColorLabel, opened?.ColorLabel, StringComparison.OrdinalIgnoreCase) Then result.ColorLabel = disk.ColorLabel
            If disk.PickState.HasValue AndAlso Not Nullable.Equals(disk.PickState, opened?.PickState) Then result.PickState = disk.PickState
            If disk.HasKeywords AndAlso
               (opened Is Nothing OrElse Not opened.HasKeywords OrElse Not SameKeywords(disk.Keywords, opened.Keywords)) Then
                result.Keywords = disk.Keywords.ToList()
                result.HasKeywords = True
            End If
            Return result
        End Function

        Private Shared Function SameKeywords(a As List(Of String), b As List(Of String)) As Boolean
            Dim left = New HashSet(Of String)(If(a, New List(Of String)()), StringComparer.OrdinalIgnoreCase)
            Return left.SetEquals(If(b, New List(Of String)()))
        End Function

        ''' <summary>Nach einer uebernommenen fremden Aenderung: die Datenbank holt den Katalogblock
        ''' aus der Datei nach, wie der Katalogindex es tut. Nur RAW und PSD tragen einen.</summary>
        Private Shared Sub SyncCatalogFromFile(file As String)
            If Not IsReadOnlyFormat(file) Then Return
            Try
                LibraryService.Instance.ImportFpxmpCatalogData(file)
            Catch ex As Exception
                DiagnosticLogService.LogException("Sidecar.SyncCatalog", ex)
            End Try
        End Sub

        ''' <summary>Legt die fremde Fassung neben die Begleitdatei, unter dem ersten freien Namen
        ''' aus <see cref="OtherVersionPaths"/>. Liegt dieselbe Fassung dort schon, bleibt es bei
        ''' der einen. Atomar ueber eine Nachbardatei und ein Umbenennen, das nichts ersetzt.
        ''' Nothing, wenn alle Namen mit anderen Fassungen belegt sind oder das Schreiben scheitert:
        ''' dann darf der Aufrufer nicht schreiben.</summary>
        Private Shared Function PreserveOtherVersion(sidecar As String, bytes As Byte()) As String
            If bytes Is Nothing Then Return Nothing
            Dim imagePath = sidecar.Substring(0, sidecar.Length - Extension.Length)
            For Each candidate In OtherVersionPaths(imagePath)
                Dim temp As String = Nothing
                Try
                    If CompanionExists(candidate) Then
                        If File.ReadAllBytes(candidate).AsSpan().SequenceEqual(bytes) Then Return candidate
                        Continue For
                    End If
                    temp = ImageProcessor.AtomicSiblingPath(candidate)
                    Using fs = New FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                        fs.Write(bytes, 0, bytes.Length)
                        fs.Flush(flushToDisk:=True)
                    End Using
                    ImageProcessor.CarryOverFileMode(sidecar, temp)
                    File.Move(temp, candidate, overwrite:=False)
                    Return candidate
                Catch ex As Exception
                    DiagnosticLogService.LogException("Sidecar.PreserveOther", ex)
                    Try
                        If temp IsNot Nothing AndAlso File.Exists(temp) Then File.Delete(temp)
                    Catch
                    End Try
                    ' Entstand der Name eben von aussen, wird der naechste versucht; jeder andere
                    ' Fehler (keine Rechte, Freigabe weg) trifft auch die uebrigen.
                    If temp Is Nothing OrElse Not File.Exists(candidate) Then Return Nothing
                End Try
            Next
            Return Nothing
        End Function

        ''' <summary>Haelt Lesen und Schreiben dieser Begleitdatei gegen andere Prozesse dieser
        ''' Anwendung zusammen: eine benannte Sperre, deren Name aus dem aufgeloesten Pfad entsteht.
        ''' Sie ist auf demselben Faden mehrfach zu nehmen, die verschachtelten Schreiber (Katalog aus
        ''' der Datenbank, Varianten) duerfen sie also erneut verlangen. Nothing, wenn sie nach
        ''' <see cref="CrossProcessLockTimeoutMs"/> noch belegt ist: dann wird nicht geschrieben.
        ''' Gibt es auf der Plattform keine benannten Sperren, bleibt es beim Vergleich vor dem
        ''' Umbenennen (ein Leerobjekt).</summary>
        Private Shared Function LockAcrossProcesses(rawPath As String) As IDisposable
            Dim sidecar = SidecarPathFor(rawPath)
            Dim key As String
            Try
                key = Path.GetFullPath(ImageProcessor.ResolveWriteTarget(sidecar))
            Catch
                key = sidecar
            End Try
            If PathIdentity.IgnoresCase Then key = key.ToUpperInvariant()
            Dim name = "FerrumPix.Sidecar." & Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).Substring(0, 40)
            Dim mutex As Threading.Mutex
            Try
                mutex = New Threading.Mutex(False, name)
            Catch ex As Exception
                DiagnosticLogService.LogException("Sidecar.CrossProcessLock", ex)
                Return New CrossProcessLock(Nothing)
            End Try
            Try
                If Not mutex.WaitOne(CrossProcessLockTimeoutMs) Then
                    mutex.Dispose()
                    DiagnosticLogService.LogAlways("Sidecar.CrossProcessLock", $"belegt, nicht geschrieben: {Path.GetFileName(sidecar)}")
                    Return Nothing
                End If
            Catch ex As Threading.AbandonedMutexException
                ' Ein anderer Prozess endete, waehrend er sie hielt: sie gehoert jetzt uns, und die
                ' Datei ist dank des atomaren Schreibens trotzdem ganz.
            End Try
            Return New CrossProcessLock(mutex)
        End Function

        Private Const CrossProcessLockTimeoutMs As Integer = 10000

        Private NotInheritable Class CrossProcessLock
            Implements IDisposable
            Private _mutex As Threading.Mutex

            Public Sub New(mutex As Threading.Mutex)
                _mutex = mutex
            End Sub

            Public Sub Dispose() Implements IDisposable.Dispose
                Dim held = _mutex
                _mutex = Nothing
                If held Is Nothing Then Return
                Try
                    held.ReleaseMutex()
                Catch ex As Exception
                    DiagnosticLogService.LogException("Sidecar.CrossProcessLock", ex)
                End Try
                held.Dispose()
            End Sub
        End Class

        ''' <summary>Der Datenbankstand wird erst unter der Schreibsperre gelesen, damit ein
        ''' wartender Schreiber keinen aelteren Katalogstand nach einem neueren zurueckschreibt.</summary>
        Friend Shared Function TryWriteCatalogFromLibrary(rawPath As String, library As LibraryService) As Boolean
            If String.IsNullOrWhiteSpace(rawPath) OrElse Not IsReadOnlyFormat(rawPath) OrElse library Is Nothing Then Return False
            SyncLock _writeLock
                Using crossLock = LockAcrossProcesses(rawPath)
                    If crossLock Is Nothing Then Return False
                    Dim catalog = library.GetSidecarCatalogData(rawPath)
                    Return TryWriteCatalog(rawPath, catalog.Rating.Value, catalog.IsFavorite.Value,
                                           catalog.ColorLabel, catalog.Keywords, catalog.PickState.Value)
                End Using
            End SyncLock
        End Function

        ''' <summary>Schreibt eine bewusste Katalogaenderung in die .fpxmp und erhaelt dabei das
        ''' vorhandene Bearbeitungsrezept. Gibt es noch keine Sidecar, wird eine mit neutralem Rezept
        ''' angelegt - Katalogdaten sind seit Formatversion 2 selbst ein legitimer Sidecar-Inhalt.
        '''
        ''' EINE VORHANDENE, ABER NICHT LESBARE DATEI BLEIBT UNANGETASTET, und das Ergebnis ist False.
        ''' Defektes XML, eine Formatversion aus einer neueren Fassung, ein unlesbares Rezept oder ein
        ''' gerade nicht erreichbarer Speicher: in jedem dieser Faelle steckt vielleicht eine ganze
        ''' Entwicklung in der Datei. Frueher wurde daraus ein neutrales Rezept, und ein einziger
        ''' Stern ersetzte sie endgueltig. Die Katalogaenderung steht dann nur in der Datenbank.</summary>
        Public Shared Function TryWriteCatalog(rawPath As String, rating As Integer, isFavorite As Boolean, colorLabel As String,
                                               keywords As IEnumerable(Of String),
                                               Optional pickState As Integer = 0) As Boolean
            If String.IsNullOrWhiteSpace(rawPath) OrElse Not IsReadOnlyFormat(rawPath) Then Return False
            ' Lesen und Schreiben unter DERSELBEN Sperre: sonst kann ein anderer Schreiber dieser
            ' Anwendung die Datei zwischen beiden aendern, und sie wuerde mit dem alten Rezept ersetzt.
            ' Dazu die Sperre ueber Prozesse hinweg (LockAcrossProcesses).
            SyncLock _writeLock
                Using crossLock = LockAcrossProcesses(rawPath)
                    If crossLock Is Nothing Then Return False
                    Dim variantId As String = Nothing
                    Dim file = VariantKey.Split(rawPath, variantId)
                    If variantId.Length > 0 Then
                        Return WriteVariantCatalog(file, variantId, New RawSidecarCatalogData With {
                            .Rating = Math.Max(0, Math.Min(5, rating)),
                            .IsFavorite = isFavorite,
                            .ColorLabel = If(colorLabel, "").Trim(),
                            .Keywords = NormalizeKeywords(keywords),
                            .HasKeywords = True,
                            .PickState = LibraryService.NormalizePickState(pickState)})
                    End If
                    Dim adjustments As ImageAdjustments = Nothing
                    Dim state = ReadRecipe(rawPath, adjustments)
                    If state = SidecarReadResult.Unreadable Then Return False
                    If state = SidecarReadResult.Missing Then adjustments = New ImageAdjustments()
                    Dim catalog = New RawSidecarCatalogData With {
                        .Rating = Math.Max(0, Math.Min(5, rating)),
                        .IsFavorite = isFavorite,
                        .ColorLabel = If(colorLabel, "").Trim(),
                        .Keywords = NormalizeKeywords(keywords),
                        .HasKeywords = True,
                        .PickState = LibraryService.NormalizePickState(pickState)
                    }
                    ' Hiess es "fehlt", wird nur angelegt, wenn am Ziel noch immer nichts liegt: eine Datei,
                    ' die ein anderes Programm inzwischen hingelegt hat, wird nicht ersetzt.
                    Return TryWriteCore(rawPath, adjustments, catalog, Nothing, Nothing,
                                        requireAbsent:=state = SidecarReadResult.Missing)
                End Using
            End SyncLock
        End Function

        ''' <summary>Liest das Rezept, aendert es und schreibt es zurueck, alles unter EINER Sperre wie
        ''' beim Katalog. Fuer Aufrufer, die ein vorhandenes Rezept ergaenzen statt es zu ersetzen (etwa
        ''' einen Drehschritt anhaengen): sonst koennte ein anderer Schreiber dieser Anwendung die Datei
        ''' zwischen Lesen und Schreiben aendern, und seine Aenderung waere weg.
        '''
        ''' Eine unlesbare Datei bleibt unangetastet (False). Fehlt sie, kommt das Rezept aus
        ''' <paramref name="createWhenMissing"/> und wird nur angelegt, wenn dort noch immer nichts
        ''' liegt.</summary>
        Public Shared Function TryModifyRecipe(rawPath As String, createWhenMissing As Func(Of ImageAdjustments),
                                               modify As Action(Of ImageAdjustments),
                                               Optional developedThumbnail As Boolean? = Nothing,
                                               Optional edited As Boolean? = Nothing) As Boolean
            Return TryModifyRecipeCore(rawPath, createWhenMissing, modify, developedThumbnail, edited) = ModifyRecipeResult.Written
        End Function

        ''' <summary>Was <see cref="TryModifyRecipeDetailed"/> herausbekam. Unreadable steht fuer sich,
        ''' weil der Aufrufer es anders melden soll als einen Schreibfehler: dort liegt eine Datei, die
        ''' wir nicht verstehen und deshalb nicht anfassen.</summary>
        Public Enum ModifyRecipeResult
            Written
            Unreadable
            Failed
        End Enum

        ''' <summary>Wie <see cref="TryModifyRecipe"/>, sagt aber, warum nichts geschrieben wurde.
        ''' <paramref name="decideEdited"/> entscheidet den Vermerk "bearbeitet" am FERTIGEN Rezept,
        ''' nach <paramref name="modify"/>, und geht vor <paramref name="edited"/>.</summary>
        Public Shared Function TryModifyRecipeDetailed(rawPath As String, createWhenMissing As Func(Of ImageAdjustments),
                                                       modify As Action(Of ImageAdjustments),
                                                       Optional developedThumbnail As Boolean? = Nothing,
                                                       Optional edited As Boolean? = Nothing,
                                                       Optional decideEdited As Func(Of ImageAdjustments, Boolean) = Nothing) As ModifyRecipeResult
            Return TryModifyRecipeCore(rawPath, createWhenMissing, modify, developedThumbnail, edited, decideEdited)
        End Function

        ''' <paramref name="createWhenMissing"/> laeuft NUR, wenn die Datei fehlt. Er darf also teuer
        ''' sein (die Startwerte einer RAW kommen aus ihren Metadaten), ein vorhandenes Rezept kostet
        ''' ihn nicht.
        Private Shared Function TryModifyRecipeCore(rawPath As String, createWhenMissing As Func(Of ImageAdjustments),
                                                    modify As Action(Of ImageAdjustments),
                                                    developedThumbnail As Boolean?,
                                                    edited As Boolean?,
                                                    Optional decideEdited As Func(Of ImageAdjustments, Boolean) = Nothing) As ModifyRecipeResult
            If String.IsNullOrWhiteSpace(rawPath) OrElse createWhenMissing Is Nothing OrElse modify Is Nothing Then Return ModifyRecipeResult.Failed
            Try
                ' Die Sperre im Prozess, die ueber Prozesse hinweg und der Vergleich vor dem
                ' Umbenennen (siehe den Block bei TryWriteChecked). Aendert ein anderes Programm die
                ' Datei zwischen Lesen und Schreiben, wird neu gelesen und die Aenderung noch einmal
                ' auf den neuen Stand gelegt, statt seinen Stand zu ersetzen.
                SyncLock _writeLock
                    Using crossLock = LockAcrossProcesses(rawPath)
                        If crossLock Is Nothing Then Return ModifyRecipeResult.Failed
                        For attempt = 1 To MaxWriteAttempts
                            ' Der Stand VOR dem Lesen: aendert sich die Datei dazwischen, faellt das
                            ' beim Vergleich vor dem Umbenennen auf.
                            Dim captured = CaptureStamp(rawPath, parse:=False)
                            Dim adjustments As ImageAdjustments = Nothing
                            Dim state = ReadRecipe(rawPath, adjustments)
                            If state = SidecarReadResult.Unreadable Then Return ModifyRecipeResult.Unreadable
                            If state = SidecarReadResult.Missing Then adjustments = createWhenMissing()
                            If adjustments Is Nothing Then Return ModifyRecipeResult.Failed
                            modify(adjustments)
                            If decideEdited IsNot Nothing Then edited = decideEdited(adjustments)
                            Dim variantId As String = Nothing
                            Dim file = VariantKey.Split(rawPath, variantId)
                            Dim written As Boolean
                            If variantId.Length > 0 Then
                                ' Eine Variante ohne Begleitdatei gibt es nicht; "fehlt" heisst hier, dass
                                ' es nichts zu aendern gibt.
                                If state <> SidecarReadResult.Read Then Return ModifyRecipeResult.Failed
                                Dim variantResult = WriteVariantRecipe(file, variantId, adjustments, edited, guard:=captured)
                                If variantResult = ModifyRecipeResult.Unreadable Then Return variantResult
                                written = variantResult = ModifyRecipeResult.Written
                            Else
                                Dim catalog = LibraryService.Instance.GetSidecarCatalogData(rawPath)
                                written = TryWriteCore(rawPath, adjustments, catalog, developedThumbnail, edited,
                                                       requireAbsent:=state = SidecarReadResult.Missing, guard:=captured)
                            End If
                            If written Then Return ModifyRecipeResult.Written
                            If CaptureStamp(rawPath, parse:=False).SameContentAs(captured) Then Return ModifyRecipeResult.Failed
                            DiagnosticLogService.LogAlways("Sidecar.ModifyRecipe",
                                $"waehrend des Schreibens von aussen geaendert, Versuch {attempt}: {Path.GetFileName(SidecarPathFor(file))}")
                        Next
                        Return ModifyRecipeResult.Failed
                    End Using
                End SyncLock
            Catch ex As Exception
                DiagnosticLogService.LogException("Sidecar.ModifyRecipe", ex)
                Return ModifyRecipeResult.Failed
            End Try
        End Function

        ''' <param name="description">Titel und die uebrigen beschreibenden Felder. Nothing laesst den
        ''' Block der vorhandenen Datei stehen: jeder andere Schreiber (Rezept, Sterne, Stichwoerter)
        ''' kennt ihn nicht, und er stammt nicht aus der Datenbank wie der Katalogblock.</param>
        ''' <param name="guard">Der Stand, auf dem der Aufrufer seine Entscheidung gebaut hat. Liegt
        ''' unmittelbar vor dem Umbenennen etwas anderes auf der Platte, wird nichts ersetzt und das
        ''' Ergebnis ist False; der Aufrufer liest dann neu (siehe WriteChecked).</param>
        Private Shared Function TryWriteCore(rawPath As String, adjustments As ImageAdjustments,
                                             catalog As RawSidecarCatalogData,
                                             developedThumbnail As Boolean?,
                                             edited As Boolean?,
                                             Optional requireAbsent As Boolean = False,
                                             Optional description As ImageDescription = Nothing,
                                             Optional imported As ForeignCatalogService.ImportedMetadata = Nothing,
                                             Optional variants As VariantSet = Nothing,
                                             Optional guard As SidecarStamp = Nothing) As Boolean
            If String.IsNullOrWhiteSpace(rawPath) OrElse adjustments Is Nothing Then Return False
            ' Die Sperre ueber Prozesse hinweg nehmen die Aufrufer: sie muss schon ueber ihrem Lesen liegen.
            SyncLock _writeLock
                Dim temp As String = Nothing
                Dim target As String = Nothing
                Try
                    ' Die geparkten Varianten kennt kein anderer Schreiber (Rezept, Sterne,
                    ' Stichwoerter): ohne Vorgabe bleibt der Block der vorhandenen Datei stehen.
                    Dim keepVariants = If(variants, ReadVariantSet(rawPath))
                    Dim variantsBlock = VariantsNode(keepVariants)
                    Dim keepDescription = If(description, TryReadDescription(rawPath))
                    Dim keepImported = If(imported, TryReadImported(rawPath))
                    Dim keepDevelopedThumbnail = If(developedThumbnail.HasValue, developedThumbnail.Value,
                                                    ReadDevelopedThumbnail(rawPath))
                    ' Der Vermerk "bearbeitet" bleibt, wie er war, wenn der Schreiber ihn nicht nennt
                    ' (Katalog: Sterne, Etikett, Stichwoerter). Fehlte er in einer aelteren Datei,
                    ' fehlt er weiter: dann entscheidet beim Lesen der Vergleich (IsEditedFile).
                    Dim keepEdited = If(edited.HasValue, edited, ReadEditedAttribute(rawPath))
                    ' HIER wird entschieden, was der Vermerk ueber gebackene Vorgaenge BEDEUTET.
                    ' Neben dieser Datei liegt kein einziges Pixel: die Quelle bleibt unangetastet,
                    ' und beim naechsten Oeffnen wird sie neu entwickelt. Also sind Entrauschen,
                    ' Objektentfernen, Retusche und Striche dort NICHT drin, sondern noch anzuwenden.
                    ' Der Editor kann das nicht setzen - er weiss beim Entrauschen noch nicht, ob
                    ' spaeter eine .fpxmp oder eine .fpx daraus wird.
                    Dim sidecarAdj = adjustments.Clone()
                    sidecarAdj.BakedOperationsApplied = False
                    Dim json = FpxService.SerializeAdjustments(sidecarAdj)
                    ' Den Katalogblock tragen nur RAW und PSD. Sterne, Etikett und Stichwoerter eines
                    ' JPG oder PNG stehen in der Datenbank und je nach Einstellung in der Datei oder
                    ' ihrer XMP; ein zweiter Ort in der .fpxmp gaebe dem Katalogindex eine neue
                    ' Vorrangfrage, und das Rezept liesse sich nicht mehr samt Datei entfernen
                    ' (TryRemoveRecipe).
                    Dim catalogBlock = If(IsReadOnlyFormat(rawPath), CatalogNode(catalog), Nothing)
                    Dim doc = New XDocument(
                        New XDeclaration("1.0", "utf-8", Nothing),
                        New XElement(Ns + "recipe",
                            New XAttribute("version", If(variantsBlock Is Nothing, FormatVersion, VariantsFormatVersion)),
                            New XAttribute("generator", "FerrumPix"),
                            If(keepDevelopedThumbnail, New XAttribute(DevelopedThumbnailAttribute, "true"), Nothing),
                            If(keepEdited.HasValue, New XAttribute(EditedAttribute, If(keepEdited.Value, "true", "false")), Nothing),
                            New XElement(Ns + "source",
                                New XAttribute("fileName", Path.GetFileName(rawPath))),
                            New XElement(Ns + "savedUtc", DateTime.UtcNow.ToString("O")),
                            catalogBlock,
                            DescriptionNode(keepDescription),
                            ImportedNode(keepImported),
                            New XElement(Ns + "adjustments",
                                New XAttribute("format", "fpx-json"),
                                New XCData(json)),
                            variantsBlock))

                    target = SidecarPathFor(rawPath)
                    ' Eine Nachbardatei je Vorgang: eine feste ".tmp" teilten sich zwei Instanzen, die
                    ' dieselbe Datei gleichzeitig schreiben, und eine haengte die halbe der anderen um.
                    ' Durch einen symbolischen Verweis hindurch schreiben und die Zugriffsrechte der
                    ' alten Datei behalten, wie jedes atomare Schreiben (ImageProcessor.WriteFileAtomic).
                    Dim finalTarget = ImageProcessor.ResolveWriteTarget(target)
                    temp = ImageProcessor.AtomicSiblingPath(finalTarget)
                    Using fs = New FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                        Using writer = XmlWriter.Create(fs, New XmlWriterSettings With {.Indent = True})
                            doc.Save(writer)
                        End Using
                        ' Bis auf die Platte, bevor umbenannt wird: sonst kann nach einem
                        ' Stromausfall der neue Name gelten und der Inhalt fehlen.
                        fs.Flush(flushToDisk:=True)
                    End Using
                    ImageProcessor.CarryOverFileMode(finalTarget, temp)
                    ' Unmittelbar vor dem Umbenennen: liegt noch der Stand da, auf dem der Aufrufer
                    ' entschieden hat? Sonst hat ein anderes Programm geschrieben, und dessen Fassung
                    ' wird nicht ersetzt.
                    If guard IsNot Nothing AndAlso Not CaptureStamp(rawPath, parse:=False).SameContentAs(guard) Then
                        DiagnosticLogService.LogAlways("Sidecar.Write",
                            $"vor dem Umbenennen von aussen geaendert, nicht ersetzt: {Path.GetFileName(target)}")
                        File.Delete(temp)
                        Return False
                    End If
                    ' Ohne Ueberschreiben wirft Move, wenn am Ziel inzwischen eine Datei liegt.
                    File.Move(temp, finalTarget, overwrite:=Not requireAbsent)
                    ' Den gemerkten Drehwinkel verwerfen, statt auf einen geaenderten Zeitstempel zu
                    ' hoffen: zwei Schreibvorgaenge kurz hintereinander koennen auf grob aufloesenden
                    ' Dateisystemen dieselbe mtime tragen.
                    ForgetDisplayFacts(target)
                    Return True
                Catch ex As Exception
                    If requireAbsent AndAlso target IsNot Nothing AndAlso File.Exists(target) Then
                        ' Kein Fehler, sondern der vereinbarte Ausgang: der Aufrufer hatte "fehlt"
                        ' gelesen, und inzwischen liegt dort eine Datei. Sie bleibt, wie sie ist.
                        DiagnosticLogService.LogAlways("Sidecar.Write",
                            $"inzwischen vorhanden, nicht ersetzt: {Path.GetFileName(target)}")
                    Else
                        DiagnosticLogService.LogException("Sidecar.Write", ex)
                    End If
                    Try
                        If temp IsNot Nothing AndAlso File.Exists(temp) Then File.Delete(temp)
                    Catch
                    End Try
                    Return False
                End Try
            End SyncLock
        End Function

        ''' <summary>Der Katalogblock, wie er in der Datei steht: fuer die Standardvariante unter dem
        ''' Wurzelelement, fuer jede zusaetzliche Variante in ihrem Eintrag.</summary>
        Private Shared Function CatalogNode(catalog As RawSidecarCatalogData) As XElement
            Dim keywordsNode = New XElement(Ns + "keywords")
            For Each keyword In NormalizeKeywords(catalog?.Keywords)
                keywordsNode.Add(New XElement(Ns + "keyword", keyword))
            Next
            Dim safeRating = Math.Max(0, Math.Min(5, If(catalog Is Nothing, 0, catalog.Rating.GetValueOrDefault())))
            Dim isFavorite = catalog IsNot Nothing AndAlso catalog.IsFavorite.GetValueOrDefault()
            Return New XElement(Ns + "catalog",
                New XElement(Ns + "rating", safeRating.ToString(Globalization.CultureInfo.InvariantCulture)),
                New XElement(Ns + "favorite", If(isFavorite, "true", "false")),
                New XElement(Ns + "colorLabel", If(catalog?.ColorLabel, "").Trim()),
                New XElement(Ns + "pick", LibraryService.NormalizePickState(
                    If(catalog Is Nothing, 0, catalog.PickState.GetValueOrDefault())).ToString(Globalization.CultureInfo.InvariantCulture)),
                keywordsNode)
        End Function

        ''' <summary>Die gemerkten Werte dieser Begleitdatei verwerfen, die der Standardvariante und
        ''' die jeder zusaetzlichen: statt auf einen geaenderten Zeitstempel zu hoffen.</summary>
        Private Shared Sub ForgetDisplayFacts(sidecar As String)
            Dim ignored As CachedDisplayFacts = Nothing
            _displayFactsCache.TryRemove(sidecar, ignored)
            For Each key In _displayFactsCache.Keys
                If key.Length > sidecar.Length AndAlso key.StartsWith(sidecar, PathIdentity.Comparison) AndAlso
                   VariantKey.IsVariant(key) Then _displayFactsCache.TryRemove(key, ignored)
            Next
            Dim ignoredList As CachedVariantList = Nothing
            _variantListCache.TryRemove(sidecar, ignoredList)
        End Sub

        Private Shared Function NormalizeKeywords(keywords As IEnumerable(Of String)) As List(Of String)
            Return If(keywords, Enumerable.Empty(Of String)()).
                Select(Function(value) If(value, "").Trim()).
                Where(Function(value) value.Length > 0).
                Distinct(StringComparer.OrdinalIgnoreCase).
                ToList()
        End Function

        ' ── Mitwandern bei Dateioperationen ──────────────────────────────────────
        ' Wie XMP-Sidecars ueblicherweise: Verschieben/Kopieren/Umbenennen/Loeschen der Bilddatei
        ' nimmt die Begleitdateien mit, und zwar ALLE drei: die eigene .fpxmp, die fremde XMP in der
        ' angehaengten Form ("foto.cr2.xmp") und in der ersetzten Form ("foto.xmp"). Frueher ging
        ' nur die .fpxmp mit; Bewertung, Stichwoerter und eine fremde Entwicklung blieben am alten
        ' Ort liegen und wurden am neuen nicht mehr gefunden. Dazu kommen die .fpdnz und die
        ' .rrdata eines anderen Entwicklers samt ihren virtuellen Kopien (CarryRrdataFiles).
        '
        ' Die fremde XMP zaehlt in jeder Schreibweise der Endung ("foto.cr2.XMP", "foto.Xmp"), nur
        ' der Name davor muss gleich sein (XmpSidecarService.ExistingXmpPath). Sie wandert unter der
        ' Endung, die sie hatte, und eine XMP am Ziel gilt auch in anderer Schreibweise als belegt.
        '
        ' Ein Fehler an einer Begleitdatei laesst die Hauptoperation stehen (sie ist schon geschehen),
        ' wird protokolliert und als Pfad an den Aufrufer zur sichtbaren Meldung zurueckgegeben.
        '
        ' DIE ERSETZTE FORM KANN MEHREREN DATEIEN GEHOEREN: "foto.xmp" passt zu foto.cr2 UND foto.jpg.
        ' Sie wird deshalb nur verschoben oder geloescht, wenn keine andere Datei desselben Stamms
        ' am alten Ort bleibt; sonst kopiert beziehungsweise stehen gelassen. Eine vorhandene XMP am
        ' Ziel wird nie ueberschrieben: sie gehoert dort womoeglich einer anderen Datei.

        Public Shared Function AccompanyMove(sourcePath As String, targetPath As String) As List(Of String)
            Dim failed As New List(Of String)()
            ' Die Bilddatei hat der Aufrufer schon verschoben; die Listen des Stapels erfahren es.
            NoteInBatch(sourcePath, added:=False)
            NoteInBatch(targetPath, added:=True)
            Try
                Dim sidecar = SidecarPathFor(sourcePath)
                ' Ueber StagedFileTransfer: unter dem Endnamen liegt nie eine halbe Datei.
                If CompanionExists(sidecar) Then StagedFileTransfer.CarryFile(sidecar, SidecarPathFor(targetPath), moveFile:=True)
            Catch ex As Exception
                DiagnosticLogService.LogException("Sidecar.AccompanyMove", ex)
                failed.Add(SidecarPathFor(sourcePath))
            End Try
            CarryOtherVersions(sourcePath, targetPath, moveFiles:=True, failed:=failed)
            CarryModelDenoiseFile(sourcePath, targetPath, moveFile:=True, failed:=failed)
            CarryXmpCompanions(sourcePath, targetPath, moveFiles:=True, failed:=failed)
            CarryRrdataFiles(sourcePath, targetPath, moveFiles:=True, failed:=failed)
            Return failed
        End Function

        Public Shared Function AccompanyCopy(sourcePath As String, targetPath As String) As List(Of String)
            Dim failed As New List(Of String)()
            NoteInBatch(targetPath, added:=True)
            Try
                Dim sidecar = SidecarPathFor(sourcePath)
                If CompanionExists(sidecar) Then StagedFileTransfer.CarryFile(sidecar, SidecarPathFor(targetPath), moveFile:=False)
            Catch ex As Exception
                DiagnosticLogService.LogException("Sidecar.AccompanyCopy", ex)
                failed.Add(SidecarPathFor(sourcePath))
            End Try
            CarryOtherVersions(sourcePath, targetPath, moveFiles:=False, failed:=failed)
            CarryModelDenoiseFile(sourcePath, targetPath, moveFile:=False, failed:=failed)
            CarryXmpCompanions(sourcePath, targetPath, moveFiles:=False, failed:=failed)
            CarryRrdataFiles(sourcePath, targetPath, moveFiles:=False, failed:=failed)
            Return failed
        End Function

        ''' <summary>Die .rrdata eines anderen Entwicklers geht mit wie die .fpxmp, die des Originals
        ''' und jede virtuelle Kopie (RrdataSidecarService.BelongsTo), unabhaengig davon, ob die
        ''' Einstellung zum Lesen an ist. Nur die Datei wird bewegt oder kopiert, ihr Inhalt bleibt
        ''' bytegleich. Eine vorhandene am Ziel wird nie ueberschrieben, sie wird gemeldet.</summary>
        Private Shared Sub CarryRrdataFiles(sourcePath As String, targetPath As String, moveFiles As Boolean,
                                            failed As List(Of String))
            Dim companions As List(Of String)
            Try
                companions = ExistingRrdataFiles(sourcePath)
            Catch ex As Exception
                ' Ordner nicht lesbar: ob eine daliegt, laesst sich nicht sagen, also gemeldet.
                DiagnosticLogService.LogException("Sidecar.CarryRrdata", ex)
                failed.Add(RrdataSidecarService.PathFor(sourcePath))
                Return
            End Try
            For Each companion In companions
                Try
                    Dim target = RrdataSidecarService.RenamedFor(companion, sourcePath, targetPath)
                    StagedFileTransfer.CarryFile(companion, target, moveFiles)
                    If moveFiles Then NoteInBatch(companion, added:=False)
                    NoteInBatch(target, added:=True)
                Catch ex As FileNotFoundException
                    ' Inzwischen von aussen entfernt (die Liste eines Stapels ist bis zu einer
                    ' Sekunde alt): dann gibt es nichts mitzunehmen.
                    NoteInBatch(companion, added:=False)
                Catch ex As Exception
                    DiagnosticLogService.LogException("Sidecar.CarryRrdata", ex)
                    failed.Add(companion)
                End Try
            Next
        End Sub

        ''' <summary>Die .rrdata zu diesem Bild, wie sie daliegen: die des Originals und die virtuellen
        ''' Kopien. Im Stapel aus der Liste des Ordners (StemListing, nach ihrer Gueltigkeit neu
        ''' gelesen), sonst aus einer Aufzaehlung: ein Stapel ueber viele Bilder eines grossen Ordners
        ''' liefe sonst fuer jedes Bild einmal durch den Bestand. Ist der Ordner nicht lesbar, fliegt
        ''' die Ausnahme weiter; die Aufrufer melden die Datei dann, statt sie still zu uebergehen.</summary>
        Friend Shared Function ExistingRrdataFiles(imagePath As String) As List(Of String)
            Dim folder = Path.GetDirectoryName(imagePath)
            If String.IsNullOrEmpty(folder) Then Return New List(Of String)()
            Dim scope = _currentStemScope.Value
            If scope Is Nothing Then
                Return RrdataSidecarService.ExistingFor(imagePath,
                    Directory.EnumerateFiles(folder, "*" & RrdataSidecarService.Extension))
            End If
            SyncLock scope.TargetListings
                Dim listing As StemListing = Nothing
                If Not scope.TargetListings.TryGetValue(folder, listing) OrElse listing.IsStale Then
                    listing = StemListing.Read(folder)
                    scope.TargetListings(folder) = listing
                End If
                Return RrdataSidecarService.ExistingFor(imagePath, listing.Files)
            End SyncLock
        End Function

        ''' <summary>Die aufgehobenen fremden Fassungen der .fpxmp (".fpxmp.other" bis ".other4",
        ''' <see cref="PreserveOtherVersion"/>) gehen mit wie die .fpxmp, jede unter ihrem Namen
        ''' am neuen Bild. Eine vorhandene am Ziel wird nie ueberschrieben, sie wird gemeldet.</summary>
        Private Shared Sub CarryOtherVersions(sourcePath As String, targetPath As String, moveFiles As Boolean,
                                              failed As List(Of String))
            Dim sources = OtherVersionPaths(sourcePath)
            Dim targets = OtherVersionPaths(targetPath)
            For index = 0 To sources.Count - 1
                Try
                    If Not CompanionExists(sources(index)) Then Continue For
                    StagedFileTransfer.CarryFile(sources(index), targets(index), moveFiles)
                Catch ex As Exception
                    DiagnosticLogService.LogException("Sidecar.CarryOther", ex)
                    failed.Add(sources(index))
                End Try
            Next
        End Sub

        ''' <summary>Die .fpdnz (ModelDenoiseFileService) geht mit wie die .fpxmp. Ihr Schluessel
        ''' kennt den Pfad nicht, nur Groesse und Aenderungszeit der RAW; behaelt die Kopie die
        ''' Aenderungszeit nicht, passt sie eben nicht mehr und das Modell rechnet. Eine vorhandene
        ''' am Ziel wird nie ueberschrieben.</summary>
        Private Shared Sub CarryModelDenoiseFile(sourcePath As String, targetPath As String, moveFile As Boolean,
                                                 failed As List(Of String))
            Dim source = ModelDenoiseFileService.PathFor(sourcePath)
            Try
                If Not CompanionExists(source) Then Return
                ' Ueber eine Teildatei: eine abgebrochene Kopie (die .fpdnz ist gross) liegt nie halb
                ' unter dem Endnamen.
                StagedFileTransfer.CarryFile(source, ModelDenoiseFileService.PathFor(targetPath), moveFile)
            Catch ex As Exception
                DiagnosticLogService.LogException("Sidecar.CarryModelDenoise", ex)
                failed.Add(source)
            End Try
        End Sub

        ''' <summary>Beim Papierkorb wandern die Begleitdateien ebenfalls in den Papierkorb (die
        ''' Bilddatei laesst sich so mitsamt Rezept wiederherstellen), sonst werden sie geloescht. Die
        ''' ersetzte XMP-Form nur, wenn keine andere Datei desselben Stamms mehr da ist. Liefert die
        ''' Pfade der Begleiter, die nicht entfernt werden konnten.</summary>
        Public Shared Function AccompanyDelete(rawPath As String, useTrash As Boolean) As List(Of String)
            Return AccompanyDeleteCore(rawPath, useTrash, AddressOf TrashService.MoveToTrash)
        End Function

        Private Shared Function AccompanyDeleteCore(rawPath As String, useTrash As Boolean,
                                                    moveToTrash As Func(Of String, Boolean)) As List(Of String)
            Dim failed As New List(Of String)()
            NoteInBatch(rawPath, added:=False)
            Dim companions As New List(Of String) From {SidecarPathFor(rawPath), rawPath & ".xmp",
                                                        ModelDenoiseFileService.PathFor(rawPath)}
            ' Die aufgehobenen fremden Fassungen der .fpxmp gehoeren ebenso zum Bild.
            companions.AddRange(OtherVersionPaths(rawPath))
            Dim stemXmp = StemXmpPathFor(rawPath)
            ' Erst nachsehen, ob es die Stamm-XMP gibt: HasStemSibling zaehlt den Ordner auf. Ist sie
            ' nicht erreichbar, wird sie gemeldet statt still uebergangen (CompanionExists).
            Dim stemExists As Boolean
            Try
                ' In der Schreibweise, in der sie daliegt ("foto.XMP"): unter diesem Namen fuehren
                ' sie der Stapel und seine Ordnerliste.
                Dim actualStem = If(stemXmp Is Nothing, Nothing, ExistingCompanion(stemXmp))
                stemExists = actualStem IsNot Nothing
                If stemExists Then stemXmp = actualStem
            Catch ex As Exception
                DiagnosticLogService.LogException("Sidecar.AccompanyDelete", ex)
                failed.Add(stemXmp)
                stemExists = False
            End Try
            If stemExists Then
                Dim scope = _currentStemScope.Value
                If scope IsNot Nothing Then
                    ' Im Stapel entscheidet erst dessen Ende, mit frisch gelesenem Ordner.
                    AddPending(scope, New PendingStemRemoval With {
                        .StemXmp = stemXmp, .ImagePath = rawPath, .UseTrash = useTrash, .MoveToTrash = moveToTrash})
                ElseIf Not HasStemSibling(rawPath) Then
                    companions.Add(stemXmp)
                End If
            End If
            ' Die .rrdata eines anderen Entwicklers samt virtuellen Kopien, wie die .xmp in den
            ' Papierkorb oder weg. Ordner nicht lesbar: gemeldet, nicht still uebergangen.
            Try
                companions.AddRange(ExistingRrdataFiles(rawPath))
            Catch ex As Exception
                DiagnosticLogService.LogException("Sidecar.AccompanyDelete", ex)
                failed.Add(RrdataSidecarService.PathFor(rawPath))
            End Try
            For Each listed In companions
                Dim companion = listed
                Try
                    companion = ExistingCompanion(listed)
                    If companion Is Nothing Then Continue For
                    If useTrash Then
                        If Not moveToTrash(companion) Then Throw New IOException("Papierkorb nicht verfuegbar: " & companion)
                    Else
                        File.Delete(companion)
                    End If
                Catch ex As Exception
                    DiagnosticLogService.LogException("Sidecar.AccompanyDelete", ex)
                    failed.Add(companion)
                End Try
            Next
            Return failed
        End Function

        ''' <summary>Liegt dort eine Begleitdatei? False NUR, wenn sie fehlt (Datei oder Ordner nicht
        ''' gefunden), und fuer einen Ordner dieses Namens. Jeder andere Fehler (Freigabe weg, keine
        ''' Rechte) fliegt als Ausnahme weiter: File.Exists sagte dann ebenfalls "fehlt", und die
        ''' Begleitdatei blieb nach dem Loeschen oder Verschieben des Bildes still am alten Ort
        ''' liegen. Die Aufrufer melden sie dann.</summary>
        Private Shared Function CompanionExists(filePath As String) As Boolean
            Try
                Return (File.GetAttributes(filePath) And FileAttributes.Directory) = 0
            Catch ex As FileNotFoundException
                Return False
            Catch ex As DirectoryNotFoundException
                Return False
            End Try
        End Function

        ''' <summary>Die Begleitdatei unter diesem Namen, wie sie auf der Platte liegt, oder Nothing.
        ''' Eine XMP auch mit anders geschriebener Endung ("foto.cr2.XMP", siehe
        ''' <see cref="XmpSidecarService.ExistingXmpPath"/>); die eigenen Begleiter (.fpxmp, .fpdnz)
        ''' schreibt nur diese Anwendung, und zwar klein. Fehler wie bei
        ''' <see cref="CompanionExists"/>.</summary>
        Private Shared Function ExistingCompanion(filePath As String) As String
            If filePath.EndsWith(".xmp", StringComparison.OrdinalIgnoreCase) Then
                Return XmpSidecarService.ExistingXmpPath(filePath, strict:=True)
            End If
            Return If(CompanionExists(filePath), filePath, Nothing)
        End Function

        ''' <summary>Der Zielname einer XMP mit der Endung in der Schreibweise der Quelle: aus
        ''' "foto.cr2.XMP" wird "neu.cr2.XMP" und nicht "neu.cr2.xmp". Das Programm, das sie angelegt
        ''' hat, sucht sie so.</summary>
        Private Shared Function WithXmpSuffixOf(target As String, actualSource As String) As String
            Return target.Substring(0, target.Length - 4) & actualSource.Substring(actualSource.Length - 4)
        End Function

        ''' <summary>Nimmt die fremden XMP-Dateien mit. Siehe den Block oben fuer die Regeln.</summary>
        Private Shared Sub CarryXmpCompanions(sourcePath As String, targetPath As String, moveFiles As Boolean,
                                              failed As List(Of String))
            ' Angehaengte Form: gehoert eindeutig dieser einen Datei.
            CarryOne(sourcePath & ".xmp", targetPath & ".xmp", moveFiles, removeSourceIfSame:=moveFiles, failed:=failed)
            ' Ersetzte Form: geteilt moeglich.
            Dim sourceStem = StemXmpPathFor(sourcePath)
            Dim targetStem = StemXmpPathFor(targetPath)
            If sourceStem Is Nothing OrElse targetStem Is Nothing Then Return
            If String.Equals(sourceStem, targetStem, PathIdentity.Comparison) Then Return
            ' Ohne Stamm-XMP gibt es nichts mitzunehmen, und die Aufzaehlung des Ordners in
            ' HasStemSibling entfaellt (siehe AccompanyDeleteCore). Nicht erreichbar heisst nicht
            ' "fehlt": dann wird sie gemeldet.
            Try
                Dim actualStem = ExistingCompanion(sourceStem)
                If actualStem Is Nothing Then Return
                sourceStem = actualStem
            Catch ex As Exception
                DiagnosticLogService.LogException("Sidecar.AccompanyXmp", ex)
                failed.Add(sourceStem)
                Return
            End Try
            targetStem = WithXmpSuffixOf(targetStem, sourceStem)
            ' Die ersetzte Form am Ziel kann einem anderen Bild gehoeren: weil dort schon eine liegt
            ' (in welcher Schreibweise auch immer), oder weil dort ein anderes Bild desselben Stamms
            ' liegt (foto.jpg neben dem neuen foto.cr2), das eine neue foto.xmp sonst ebenso faende
            ' und fremde Bewertungen und Stichwoerter uebernaehme. Der eindeutige Name nimmt die
            ' Quell-XMP dann auf und hat beim Lesen Vorrang. Im Zweifel (Ordner nicht lesbar) der
            ' eindeutige Name.
            Dim stemTaken = XmpSidecarService.ExistingXmpPath(targetStem, strict:=False) IsNot Nothing
            Dim targetHasSibling = Not stemTaken AndAlso HasStemSiblingInBatch(targetPath, whenUnknown:=True)
            If (stemTaken OrElse targetHasSibling) AndAlso
               XmpSidecarService.ExistingXmpPath(targetPath & ".xmp", strict:=False) Is Nothing Then
                targetStem = WithXmpSuffixOf(targetPath & ".xmp", sourceStem)
            ElseIf targetHasSibling Then
                ' Der eindeutige Name ist schon belegt (die angehaengte Form kam eben mit), die
                ' Stamm-Form wuerde dem Nachbarn zugeordnet: kein freier Platz, die Quell-XMP bleibt
                ' und wird gemeldet, wie bei einer fremden XMP am Ziel.
                DiagnosticLogService.LogAlways("Sidecar.Accompany",
                    $"Stamm-XMP nicht mitgenommen, am Ziel liegt ein Bild desselben Stamms: {Path.GetFileName(targetStem)}")
                failed.Add(sourceStem)
                Return
            End If
            Dim scope = _currentStemScope.Value
            If moveFiles AndAlso scope IsNot Nothing Then
                ' Im Stapel nur kopieren und vormerken: ob die Quelle gehen darf, entscheidet das Ende
                ' mit frisch gelesenem Ordner. Liegt dieselbe Datei schon am Ziel (das Geschwister
                ' zog im selben Stapel vorher um), ist das kein Fehler.
                CarryOne(sourceStem, targetStem, moveFile:=False, removeSourceIfSame:=False, failed:=failed, sameIsDone:=True)
                AddPending(scope, New PendingStemRemoval With {.StemXmp = sourceStem, .ImagePath = sourcePath, .CopyAt = targetStem})
                Return
            End If
            ' Beim Kopieren bleibt die Quell-XMP ohnehin, ob ein Geschwister da ist oder nicht.
            Dim siblingStays = moveFiles AndAlso HasStemSibling(sourcePath)
            CarryOne(sourceStem, targetStem, moveFiles AndAlso Not siblingStays,
                     removeSourceIfSame:=moveFiles AndAlso Not siblingStays, failed:=failed)
        End Sub

        Private Shared Sub CarryOne(source As String, target As String, moveFile As Boolean, removeSourceIfSame As Boolean,
                                    failed As List(Of String), Optional sameIsDone As Boolean = False)
            Try
                ' Die Quelle in ihrer Schreibweise, und das Ziel bekommt deren Endung. Eine XMP am Ziel
                ' zaehlt in jeder Schreibweise als vorhanden: daneben eine zweite anzulegen hiesse,
                ' dass zwei Dateien dasselbe Bild beschreiben und je nach Programm die eine oder die
                ' andere gilt.
                Dim actualSource = ExistingCompanion(source)
                If actualSource Is Nothing Then Return
                source = actualSource
                target = WithXmpSuffixOf(target, source)
                Dim existingTarget = XmpSidecarService.ExistingXmpPath(target, strict:=False)
                ' Die Quelle selbst ist kein belegtes Ziel.
                If existingTarget IsNot Nothing AndAlso String.Equals(existingTarget, source, PathIdentity.Comparison) Then existingTarget = Nothing
                If existingTarget IsNot Nothing Then
                    target = existingTarget
                    ' Liegt dort schon DIESELBE Datei (ein Stapel, der foto.cr2 und foto.jpg nacheinander
                    ' umbenennt, hat sie beim ersten Schritt kopiert), bleibt die Quelle sonst als Waise
                    ' zurueck. Eine ANDERE Datei am Ziel gehoert jemand anderem und bleibt.
                    Dim same = (removeSourceIfSame OrElse sameIsDone) AndAlso
                               File.ReadAllBytes(source).AsSpan().SequenceEqual(File.ReadAllBytes(target))
                    If same AndAlso removeSourceIfSame Then
                        File.Delete(source)
                    ElseIf same Then
                        ' Schon da, nichts zu tun.
                    Else
                        DiagnosticLogService.LogAlways("Sidecar.Accompany",
                            $"XMP am Ziel vorhanden, nicht ueberschrieben: {Path.GetFileName(target)}")
                        failed.Add(source)
                    End If
                    Return
                End If
                StagedFileTransfer.CarryFile(source, target, moveFile)
                XmpSidecarService.ForgetFolderListing(Path.GetDirectoryName(target))
            Catch ex As Exception
                DiagnosticLogService.LogException("Sidecar.AccompanyXmp", ex)
                failed.Add(source)
            End Try
        End Sub

        ''' <summary>Ein verwaister Begleiter darf nicht ersetzt und eine fremde Stamm-XMP nicht
        ''' an ein Bild ohne eigene XMP angehaengt werden. Liefert den kollidierenden Pfad.
        '''
        ''' <paramref name="replacingTarget"/>: das vorhandene Ziel ist zum Ersetzen freigegeben. Dann
        ''' gehoert seine Stamm-XMP ("ziel/foto.xmp" neben "ziel/foto.cr2") ihm und geht mit ihm
        ''' beiseite (<see cref="OwnedStemXmp"/>), statt den Vorgang aufzuhalten - es sei denn, ein
        ''' anderes Bild desselben Stamms liegt dort und kann sie ebenso meinen.</summary>
        Public Shared Function UnrelatedTargetSidecar(sourcePath As String, targetPath As String,
                                                      Optional replacingTarget As Boolean = False) As String
            If Not File.Exists(targetPath) AndAlso File.Exists(SidecarPathFor(targetPath)) Then Return SidecarPathFor(targetPath)
            ' Ebenso eine verwaiste .rrdata eines anderen Entwicklers: das Bild bekaeme sonst ihre
            ' Angaben, und die eigene bliebe am alten Ort.
            If Not File.Exists(targetPath) AndAlso Not String.Equals(sourcePath, targetPath, PathIdentity.Comparison) AndAlso
               File.Exists(RrdataSidecarService.PathFor(targetPath)) Then Return RrdataSidecarService.PathFor(targetPath)
            ' Die XMP in jeder Schreibweise der Endung (XmpSidecarService.ExistingXmpPath).
            If Not File.Exists(targetPath) AndAlso Not String.Equals(sourcePath, targetPath, PathIdentity.Comparison) Then
                Dim targetAppended = XmpSidecarService.ExistingXmpPath(targetPath & ".xmp", strict:=False)
                If targetAppended IsNot Nothing Then Return targetAppended
            End If
            Dim sourceStem = StemXmpPathFor(sourcePath)
            Dim targetStem = StemXmpPathFor(targetPath)
            If targetStem Is Nothing Then Return Nothing
            If String.Equals(sourceStem, targetStem, PathIdentity.Comparison) Then Return Nothing
            Dim existingTargetStem = XmpSidecarService.ExistingXmpPath(targetStem, strict:=False)
            If existingTargetStem Is Nothing Then Return Nothing
            If replacingTarget AndAlso File.Exists(targetPath) AndAlso OwnedStemXmp(targetPath) IsNot Nothing Then Return Nothing
            If XmpSidecarService.ExistingXmpPath(sourcePath & ".xmp", strict:=False) Is Nothing AndAlso
               (sourceStem Is Nothing OrElse XmpSidecarService.ExistingXmpPath(sourceStem, strict:=False) Is Nothing) Then Return existingTargetStem
            Return Nothing
        End Function

        ''' <summary>Die Stamm-XMP ("ordner/foto.xmp"), wenn sie vorhanden ist und allein dieser Datei
        ''' gehoert, weil kein anderes Bild desselben Stamms daneben liegt. Sonst Nothing: dann kann
        ''' sie ebenso einem Nachbarn gehoeren (foto.jpg neben foto.cr2) und wird nicht angefasst.
        '''
        ''' Im Stapel wird der Ordner dafuer nur einmal gelesen: beim Ersetzen vieler Bilder lief
        ''' sonst fuer jedes eine Aufzaehlung des Zielordners. Sagt die gemerkte Liste faelschlich
        ''' "allein", geht die XMP beiseite, und ihr Loeschen entscheidet erst das Ende des Stapels mit
        ''' frisch gelesenem Ordner (<see cref="DeferAsideStemRemoval"/>); verloren geht nichts.</summary>
        Public Shared Function OwnedStemXmp(imagePath As String) As String
            ' In der Schreibweise, in der sie daliegt (auch "foto.XMP").
            Dim stem = XmpSidecarService.ExistingXmpPath(StemXmpPathFor(imagePath), strict:=False)
            If stem Is Nothing Then Return Nothing
            ' Im Zweifel geteilt: dann bleibt sie, wo sie ist.
            If HasStemSiblingInBatch(imagePath, whenUnknown:=True) Then Return Nothing
            Return stem
        End Function

        ''' <summary>HasStemSibling, im Stapel aus der Liste des Ordners (StemListing, nach ihrer
        ''' Gueltigkeit neu gelesen) statt mit einer Aufzaehlung je Bild. Laesst sich der Ordner nicht
        ''' lesen, gilt <paramref name="whenUnknown"/>: der Aufrufer waehlt die sichere Seite.</summary>
        Private Shared Function HasStemSiblingInBatch(imagePath As String, whenUnknown As Boolean) As Boolean
            Dim scope = _currentStemScope.Value
            If scope Is Nothing Then Return HasStemSibling(imagePath)
            Dim folder = Path.GetDirectoryName(imagePath)
            If String.IsNullOrEmpty(folder) Then Return whenUnknown
            Dim sameStem As List(Of String)
            Try
                SyncLock scope.TargetListings
                    Dim listing As StemListing = Nothing
                    If Not scope.TargetListings.TryGetValue(folder, listing) OrElse listing.IsStale Then
                        listing = StemListing.Read(folder)
                        scope.TargetListings(folder) = listing
                    End If
                    sameStem = listing.WithStem(Path.GetFileNameWithoutExtension(imagePath))
                End SyncLock
            Catch ex As Exception
                DiagnosticLogService.LogException("Sidecar.HasStemSiblingInBatch", ex)
                Return whenUnknown
            End Try
            Return HasStemSiblingAmong(sameStem, imagePath)
        End Function

        ''' <summary>Nach einem gelungenen Ersetzen: die mit dem alten Ziel beiseitegelegte Stamm-XMP
        ''' nicht sofort loeschen, sondern am Ende des Stapels mit frisch gelesenem Ordner. Hat das Bild
        ''' am Ziel dann doch ein Geschwister, gehoerte die XMP auch ihm und kommt an ihren Namen
        ''' zurueck. True, wenn vorgemerkt (im Stapel); False ausserhalb, dann loescht der Aufrufer.</summary>
        Public Shared Function DeferAsideStemRemoval(asideStem As String, originalStem As String, imagePath As String) As Boolean
            Dim scope = _currentStemScope.Value
            If scope Is Nothing Then Return False
            AddPending(scope, New PendingStemRemoval With {.StemXmp = asideStem, .AsideOriginal = originalStem, .ImagePath = imagePath})
            Return True
        End Function

        ''' <summary>"ordner/foto.cr2" -> "ordner/foto.xmp"; Nothing, wenn die Datei selbst eine .xmp
        ''' ist oder keine Endung hat (dann gibt es keine ersetzte Form).</summary>
        Friend Shared Function StemXmpPathFor(imagePath As String) As String
            If String.IsNullOrWhiteSpace(imagePath) Then Return Nothing
            Dim ext = Path.GetExtension(imagePath)
            If String.IsNullOrEmpty(ext) OrElse String.Equals(ext, ".xmp", StringComparison.OrdinalIgnoreCase) Then Return Nothing
            Return Path.ChangeExtension(imagePath, ".xmp")
        End Function

        ' ── Stamm-XMP im Stapel ──────────────────────────────────────────────────
        ' HasStemSibling zaehlt den Ordner auf, und unter Unix liest die Aufzaehlung dafuer ALLE
        ' Eintraege, das Namensmuster filtert erst danach. Ein Stapel ueber viele Bilder mit
        ' Stamm-XMP in einem grossen Ordner lief so fuer jedes Bild einmal durch den Bestand, auf der
        ' Netzfreigabe quadratisch langsam.
        '
        ' Eine gemerkte Ordnerliste waere schnell, aber falsch in der gefaehrlichen Richtung: kommt
        ' waehrenddessen von aussen foto.jpg neben foto.cr2 und foto.xmp, saehe sie das Geschwister
        ' nicht, und die geteilte XMP wuerde verschoben oder geloescht. Innerhalb eines Stapels
        ' (BatchScope) wird deshalb gar nicht mehr nach Geschwistern gefragt: die Stamm-XMP wird
        ' beim Verschieben nur KOPIERT, beim Loeschen stehen gelassen, und beides vorgemerkt. Erst am
        ' Ende des Stapels liest Finish jeden betroffenen Ordner EINMAL frisch und entfernt, was dann
        ' wirklich kein Geschwister mehr hat. Eine Fehlentscheidung kostet so hoechstens eine Kopie zu
        ' viel, nie eine verlorene XMP. Ausserhalb eines Stapels wird wie bisher sofort gefragt.

        Friend NotInheritable Class PendingStemRemoval
            ''' Die Stamm-XMP am alten Ort.
            Friend StemXmp As String
            ''' Das Bild, das umgezogen oder geloescht ist; es zaehlt nicht als Geschwister.
            Friend ImagePath As String
            ''' Beim Verschieben die Kopie am Ziel. Entfernt wird nur, wenn sie bytegleich da ist.
            Friend CopyAt As String
            ''' Beim Loeschen: in den Papierkorb statt endgueltig.
            Friend UseTrash As Boolean
            Friend MoveToTrash As Func(Of String, Boolean)
            ''' Beim Ersetzen eines Ziels: der urspruengliche Name der beiseitegelegten Stamm-XMP
            ''' (StemXmp ist dann die beiseitegelegte Datei, ImagePath das neue Bild am Ziel).
            Friend AsideOriginal As String
        End Class

        ''' <summary>Ein laufender Stapel (<see cref="BatchScope"/>). <see cref="Finish"/> entscheidet
        ''' ueber die vorgemerkten Stamm-XMP und liefert, was sich nicht entfernen liess; der Aufrufer
        ''' ruft es am Ende der Schleife IM HINTERGRUND und meldet das Ergebnis. Dispose beendet den
        ''' Rahmen und holt Finish nur nach, wenn es nicht gerufen wurde (etwa nach einer Ausnahme);
        ''' dann bleibt es beim Protokoll.</summary>
        Public NotInheritable Class SidecarBatch
            Implements IDisposable

            Friend ReadOnly Pending As New List(Of PendingStemRemoval)()
            ''' Zielordner, gelesen und nach Stamm geordnet, fuer OwnedStemXmp; nach
            ''' StemListing.MaxAgeSeconds neu gelesen.
            Friend ReadOnly TargetListings As New Dictionary(Of String, StemListing)(PathIdentity.Comparer)
            Private ReadOnly _previous As SidecarBatch
            Private _finished As Boolean
            Private _disposed As Boolean

            Friend Sub New(previous As SidecarBatch)
                _previous = previous
            End Sub

            ''' <summary>Entfernt die vorgemerkten Stamm-XMP, die kein Geschwister mehr haben, und
            ''' liefert die Pfade derer, bei denen das scheiterte. Ein zweiter Aufruf tut nichts.
            ''' Liest Ordner und Dateien: nicht auf dem Anzeigefaden rufen.</summary>
            Public Function Finish() As List(Of String)
                Dim toFinish As List(Of PendingStemRemoval)
                SyncLock Pending
                    If _finished Then Return New List(Of String)()
                    _finished = True
                    toFinish = Pending.ToList()
                    Pending.Clear()
                End SyncLock
                Return FinishPendingStemRemovals(toFinish)
            End Function

            ''' <summary>Beendet den Rahmen. Wurde Finish nicht gerufen, laeuft es hier im Hintergrund
            ''' nach, nie auf dem Faden des Aufrufers (das kann der Anzeigefaden sein); was dann
            ''' scheitert, steht nur im Protokoll. Die Aufrufer rufen Finish deshalb auch nach einer
            ''' Ausnahme selbst.</summary>
            Public Sub Dispose() Implements IDisposable.Dispose
                If _disposed Then Return
                _disposed = True
                _currentStemScope.Value = _previous
                Dim open As Boolean
                SyncLock Pending
                    open = Not _finished
                End SyncLock
                If Not open Then Return
                Task.Run(Sub()
                             Dim failed = Finish()
                             If failed.Count > 0 Then
                                 DiagnosticLogService.LogAlways("Sidecar.FinishStemXmp", "nicht entfernt: " & String.Join(", ", failed))
                             End If
                         End Sub)
            End Sub
        End Class

        ''' <summary>Ein Ordner, nach Stamm geordnet, mit dem Zeitpunkt des Lesens. Aelter als
        ''' <see cref="MaxAgeSeconds"/> wird er neu gelesen: so sieht eine Entscheidung, was von aussen
        ''' dazukam, spaetestens nach dieser Zeit, und ein grosser Ordner wird trotzdem hoechstens
        ''' einmal je Zeitspanne gelesen statt je Bild.</summary>
        Friend NotInheritable Class StemListing
            Friend Const MaxAgeSeconds As Double = 1.0
            ''' Stamm -> Pfade mit diesem Stamm.
            Private ReadOnly _byStem As New Dictionary(Of String, List(Of String))(PathIdentity.Comparer)
            ''' Alle Dateien des Ordners, fuer die Frage, ob eine bestimmte da ist.
            Private ReadOnly _all As New HashSet(Of String)(PathIdentity.Comparer)
            ''' Beginn des Lesens: was danach von AUSSEN entstand, kennt die Liste nicht. Was der
            ''' eigene Stapel anlegt oder wegnimmt, traegt er nach (Add, Remove).
            Friend ReadUtc As DateTime
            Friend ReadSeconds As Double

            Friend Function Contains(filePath As String) As Boolean
                Return _all.Contains(filePath)
            End Function

            ''' <summary>Alle Dateien des Ordners; nur unter der Sperre des Stapels lesen.</summary>
            Friend ReadOnly Property Files As IEnumerable(Of String)
                Get
                    Return _all
                End Get
            End Property

            ''' <summary>Die Pfade mit diesem Stamm, als Kopie.</summary>
            Friend Function WithStem(stem As String) As List(Of String)
                Dim paths As List(Of String) = Nothing
                Return If(_byStem.TryGetValue(stem, paths), paths.ToList(), New List(Of String)())
            End Function

            Friend Sub Add(filePath As String)
                If Not _all.Add(filePath) Then Return
                Dim stem = Path.GetFileNameWithoutExtension(filePath)
                Dim paths As List(Of String) = Nothing
                If Not _byStem.TryGetValue(stem, paths) Then
                    paths = New List(Of String)()
                    _byStem(stem) = paths
                End If
                paths.Add(filePath)
            End Sub

            Friend Sub Remove(filePath As String)
                If Not _all.Remove(filePath) Then Return
                Dim paths As List(Of String) = Nothing
                If _byStem.TryGetValue(Path.GetFileNameWithoutExtension(filePath), paths) Then
                    paths.RemoveAll(Function(p) String.Equals(p, filePath, PathIdentity.Comparison))
                End If
            End Sub

            ''' <summary>Aelter als eine Sekunde, mindestens aber als das Vierfache der Lesezeit. Ohne
            ''' den zweiten Teil waere eine Liste, deren Lesen auf einer langsamen Freigabe laenger
            ''' dauert als die Sekunde, schon bei der Rueckgabe veraltet, und jedes Bild laese den
            ''' Ordner neu. So geht hoechstens ein Fuenftel der Zeit ins Lesen.</summary>
            Friend ReadOnly Property IsStale As Boolean
                Get
                    Return (DateTime.UtcNow - ReadUtc).TotalSeconds > Math.Max(MaxAgeSeconds, 4.0 * ReadSeconds)
                End Get
            End Property

            Friend Shared Function Read(folder As String) As StemListing
                Dim readUtc = DateTime.UtcNow
                Dim listing = New StemListing With {.ReadUtc = readUtc}
                For Each file In Directory.EnumerateFiles(folder)
                    listing.Add(file)
                Next
                listing.ReadSeconds = (DateTime.UtcNow - readUtc).TotalSeconds
                Return listing
            End Function
        End Class

        ''' <summary>Traegt in die Zielliste des laufenden Stapels nach, was er selbst in einen
        ''' Ordner gelegt oder daraus genommen hat. Ohne das saehe ein Stapel, der erst foto.jpg und
        ''' danach foto.cr2 samt foto.xmp ins selbe Ziel kopiert, das eben kopierte JPEG nicht, und
        ''' die XMP landete unter dem gemeinsamen Stamm. Ohne Stapel oder ohne Liste nichts.</summary>
        Private Shared Sub NoteInBatch(filePath As String, added As Boolean)
            Dim scope = _currentStemScope.Value
            If scope Is Nothing OrElse String.IsNullOrEmpty(filePath) Then Return
            Dim folder = Path.GetDirectoryName(filePath)
            If String.IsNullOrEmpty(folder) Then Return
            SyncLock scope.TargetListings
                Dim listing As StemListing = Nothing
                If Not scope.TargetListings.TryGetValue(folder, listing) Then Return
                If added Then listing.Add(filePath) Else listing.Remove(filePath)
            End SyncLock
        End Sub

        ' AsyncLocal und nicht je Faden: die Stapel laufen ueber Await und Task.Run.
        Private Shared ReadOnly _currentStemScope As New Threading.AsyncLocal(Of SidecarBatch)()

        ''' <summary>Fuer die Dauer des Using entscheidet ein Stapel ueber geteilte Stamm-XMP erst an
        ''' seinem Ende (siehe den Block oben). Um die Schleife eines Stapels legen, der Bilder
        ''' verschiebt, umbenennt oder loescht, und danach <see cref="SidecarBatch.Finish"/> rufen.</summary>
        Public Shared Function BatchScope() As SidecarBatch
            Dim scope = New SidecarBatch(_currentStemScope.Value)
            _currentStemScope.Value = scope
            Return scope
        End Function

        Private Shared Sub AddPending(scope As SidecarBatch, removal As PendingStemRemoval)
            SyncLock scope.Pending
                scope.Pending.Add(removal)
            End SyncLock
        End Sub

        ''' <summary>Das Ende eines Stapels: Ordner fuer Ordner EINE frische Aufzaehlung, und gleich
        ''' danach wird jede vorgemerkte Stamm-XMP dieses Ordners entfernt, deren Bild kein Geschwister
        ''' mehr hat und die beim Verschieben bytegleich am Ziel liegt. Liefert die Pfade, bei denen das
        ''' Entfernen scheiterte; was unsicher ist, bleibt stehen und zaehlt nicht als Fehler.
        '''
        ''' Je Ordner gelesen und SOFORT abgearbeitet, nicht alle Ordner vorweg: die Zeit zwischen
        ''' Lesen und Entfernen ist so nur die eines Ordners. Ganz schliessen laesst sie sich nicht,
        ''' das gilt fuer jede Pruefung vor einer Dateioperation, auch ausserhalb eines Stapels. Liegt
        ''' am urspruenglichen Bildpfad inzwischen wieder eine Datei, gehoert die XMP ihr und bleibt.</summary>
        Private Shared Function FinishPendingStemRemovals(pending As List(Of PendingStemRemoval)) As List(Of String)
            Dim failed As New List(Of String)()
            If pending Is Nothing OrElse pending.Count = 0 Then Return failed
            For Each folderGroup In pending.GroupBy(Function(r) If(Path.GetDirectoryName(r.ImagePath), ""), PathIdentity.Comparer)
                If String.IsNullOrEmpty(folderGroup.Key) Then Continue For
                ' Nach Stamm geordnet: ein grosser Ordner wird gelesen und dann je Bild nur noch
                ' nachgeschlagen. Dauert das Abarbeiten (der Papierkorb ist unter Linux ein eigener
                ' Prozess je Datei), wird nach StemListing.MaxAgeSeconds neu gelesen.
                Dim listing As StemListing = Nothing
                For Each removal In folderGroup
                    Try
                        ' ERST DEN ORDNER LESEN, dann fragen, was da ist. File.Exists sagt auch bei
                        ' einer weggebrochenen Freigabe "fehlt", und die XMP waere still uebergangen
                        ' worden; die Aufzaehlung wirft dann und wird gemeldet.
                        If listing Is Nothing OrElse listing.IsStale Then
                            Try
                                listing = StemListing.Read(folderGroup.Key)
                            Catch ex As Exception
                                ' Ordner nicht lesbar: im Zweifel bleiben alle stehen, und der
                                ' Nutzer erfaehrt es. Ob sie noch da sind, laesst sich nicht sagen.
                                DiagnosticLogService.LogException("Sidecar.FinishStemXmp", ex)
                                failed.AddRange(folderGroup.SkipWhile(Function(r) r IsNot removal).Select(Function(r) r.StemXmp).
                                                Except(failed, PathIdentity.Comparer))
                                Exit For
                            End Try
                        End If
                        ' ALLEIN DIE LISTE sagt, was da ist, nachgefuehrt um das, was dieser Abschluss
                        ' selbst entfernt oder zurueckgelegt hat (zwei Bilder desselben Stamms hatten
                        ' dieselbe XMP vorgemerkt). Kein File.Exists: das sagt bei einer Freigabe, die
                        ' nach dem Lesen wegbricht, still "fehlt". Ist sie weg, wirft der Versuch
                        ' unten, und die XMP wird gemeldet.
                        If Not listing.Contains(removal.StemXmp) Then Continue For
                        Dim sameStem = listing.WithStem(Path.GetFileNameWithoutExtension(removal.ImagePath))
                        If removal.AsideOriginal IsNot Nothing Then
                            ' Beiseitegelegt beim Ersetzen: das neue Bild am Ziel zaehlt nicht als
                            ' Geschwister, jedes andere schon. Dann gehoerte die XMP auch ihm.
                            If HasStemSiblingAmong(sameStem, removal.ImagePath) Then
                                If listing.Contains(removal.AsideOriginal) Then
                                    ' Der Name ist inzwischen belegt: die alte Fassung bleibt unter dem
                                    ' Beiseite-Namen liegen und wird gemeldet.
                                    failed.Add(removal.StemXmp)
                                Else
                                    File.Move(removal.StemXmp, removal.AsideOriginal)
                                    XmpSidecarService.ForgetFolderListing(folderGroup.Key)
                                    listing.Remove(removal.StemXmp)
                                    listing.Add(removal.AsideOriginal)
                                End If
                            Else
                                DeleteExisting(removal.StemXmp)
                                listing.Remove(removal.StemXmp)
                            End If
                            Continue For
                        End If
                        If listing.Contains(removal.ImagePath) Then Continue For
                        If HasStemSiblingAmong(sameStem, removal.ImagePath) Then Continue For
                        If removal.CopyAt IsNot Nothing Then
                            ' Die Kopie liegt in einem anderen Ordner. Fehlt sie, bleibt die Quelle und
                            ' wird gemeldet; ist sie anders (eine fremde XMP am Ziel), hat das Mitnehmen
                            ' das schon gemeldet.
                            If Not File.Exists(removal.CopyAt) Then Throw New IOException("Kopie am Ziel nicht da: " & removal.CopyAt)
                            If Not File.ReadAllBytes(removal.StemXmp).AsSpan().SequenceEqual(File.ReadAllBytes(removal.CopyAt)) Then Continue For
                            DeleteExisting(removal.StemXmp)
                        ElseIf removal.UseTrash Then
                            If Not removal.MoveToTrash(removal.StemXmp) Then Throw New IOException("Papierkorb nicht verfuegbar: " & removal.StemXmp)
                        Else
                            DeleteExisting(removal.StemXmp)
                        End If
                        listing.Remove(removal.StemXmp)
                    Catch ex As Exception
                        DiagnosticLogService.LogException("Sidecar.FinishStemXmp", ex)
                        failed.Add(removal.StemXmp)
                    End Try
                Next
            Next
            Return failed
        End Function

        ''' <summary>Loescht eine Datei, von der die Liste sagt, dass sie da ist. File.Delete wirft
        ''' bei einer fehlenden Datei NICHT; ist sie danach nicht weg, ebenso wenig. Deshalb danach
        ''' geprueft: steht sie noch da oder laesst sich das nicht sagen, ist es ein Fehler.</summary>
        Private Shared Sub DeleteExisting(filePath As String)
            File.Delete(filePath)
            If File.Exists(filePath) Then Throw New IOException("nicht entfernt: " & filePath)
        End Sub

        ''' <summary>Liegt neben der Datei noch eine ANDERE Datei desselben Stamms (foto.jpg neben
        ''' foto.cr2)? Begleitdateien und die Datei selbst zaehlen nicht.</summary>
        Private Shared Function HasStemSibling(imagePath As String) As Boolean
            Try
                Dim folder = Path.GetDirectoryName(imagePath)
                Dim stem = Path.GetFileNameWithoutExtension(imagePath)
                If String.IsNullOrEmpty(folder) OrElse Not Directory.Exists(folder) Then Return False
                Return HasStemSiblingAmong(Directory.EnumerateFiles(folder, stem & ".*"), imagePath)
            Catch ex As Exception
                ' Im Zweifel annehmen, dass eine andere Datei die XMP noch braucht: dann wird kopiert
                ' statt verschoben und nicht geloescht - lieber eine Kopie zu viel als eine verloren.
                DiagnosticLogService.LogException("Sidecar.HasStemSibling", ex)
                Return True
            End Try
        End Function

        ''' <summary>Dieselbe Frage ueber eine schon gelesene Liste von Pfaden desselben Ordners.</summary>
        Private Shared Function HasStemSiblingAmong(candidates As IEnumerable(Of String), imagePath As String) As Boolean
            Dim stem = Path.GetFileNameWithoutExtension(imagePath)
            For Each candidate In candidates
                If String.Equals(candidate, imagePath, PathIdentity.Comparison) Then Continue For
                If Not String.Equals(Path.GetFileNameWithoutExtension(candidate), stem, PathIdentity.Comparison) Then Continue For
                Dim ext = Path.GetExtension(candidate)
                If String.Equals(ext, ".xmp", StringComparison.OrdinalIgnoreCase) OrElse
                   String.Equals(ext, Extension, StringComparison.OrdinalIgnoreCase) Then Continue For
                Return True
            Next
            Return False
        End Function

        ''' <summary>Liegt neben einem Bild, das selbst keine RAW ist, eine RAW desselben Stamms
        ''' (foto.cr2 neben foto.jpg)? Dann gehoert die ersetzte Form "foto.xmp" der RAW, und das Bild
        ''' liest und schreibt nur seine angehaengte "foto.jpg.xmp" (XmpSidecarService.FindSidecar,
        ''' SidecarPathToCreate, FolderSidecarStamps.StampFor). Dieselbe Stammfrage wie
        ''' <see cref="HasStemSibling"/>, nur dass allein eine RAW zaehlt
        ''' (<see cref="RawPreviewService.IsCameraRaw"/>).
        '''
        ''' Ist die Datei selbst eine RAW, False: ihr gehoert die Stamm-XMP wie bisher, auch neben
        ''' einer zweiten RAW.
        '''
        ''' Laesst sich der Ordner nicht auflisten (Ausfuehr-, aber kein Leserecht), wird je
        ''' RAW-Endung nach dem Namen gefragt, klein und gross geschrieben; das geht ohne Leserecht.
        ''' Eine Endung in gemischter Schreibweise ("foto.Cr2") sieht nur die Auflistung. Scheitert
        ''' auch das, True: eine XMP, die einer anderen Datei gehoeren kann, wird im Zweifel nicht
        ''' angefasst.</summary>
        Friend Shared Function HasCameraRawStemSibling(imagePath As String) As Boolean
            If String.IsNullOrWhiteSpace(imagePath) OrElse RawPreviewService.IsCameraRaw(imagePath) Then Return False
            Dim folder As String = Nothing
            Dim stem As String = Nothing
            Try
                folder = Path.GetDirectoryName(imagePath)
                stem = Path.GetFileNameWithoutExtension(imagePath)
                If String.IsNullOrEmpty(folder) OrElse Not Directory.Exists(folder) Then Return False
                Return HasCameraRawStemSiblingAmong(Directory.EnumerateFiles(folder, stem & ".*"), imagePath)
            Catch ex As Exception
                DiagnosticLogService.LogException("Sidecar.HasCameraRawStemSibling", ex)
            End Try
            Try
                For Each ext In RawPreviewService.SupportedExtensions
                    If File.Exists(Path.Combine(folder, stem & ext)) Then Return True
                    If Not PathIdentity.IgnoresCase AndAlso File.Exists(Path.Combine(folder, stem & ext.ToUpperInvariant())) Then Return True
                Next
                Return False
            Catch ex As Exception
                DiagnosticLogService.LogException("Sidecar.HasCameraRawStemSibling", ex)
                Return True
            End Try
        End Function

        ''' <summary>Dieselbe Frage ueber eine schon gelesene Liste von Pfaden desselben Ordners.</summary>
        Friend Shared Function HasCameraRawStemSiblingAmong(candidates As IEnumerable(Of String), imagePath As String) As Boolean
            If RawPreviewService.IsCameraRaw(imagePath) Then Return False
            Return HasStemSiblingAmong(candidates.Where(Function(candidate) RawPreviewService.IsCameraRaw(candidate)), imagePath)
        End Function

        ''' <summary>Nur die Drehung aus dem Sidecar (0/90/180/270; 0 wenn keiner da ist). Der
        ''' ANZEIGE-Weg braucht ausschliesslich diesen einen Wert: Viewer, Filmstreifen und Kacheln
        ''' zeigen die schnelle eingebettete RAW-Vorschau, nicht die entwickelte Datei - Belichtung
        ''' und Farbe aus dem Rezept wirken dort bewusst nicht, die Geometrie muss aber stimmen,
        ''' sonst haette das Drehen im Viewer sichtbar keine Wirkung.
        '''
        ''' Ergebnisse werden je Sidecar-Zeitstempel gemerkt: der Aufruf sitzt im Thumbnail-Pfad und
        ''' liefe sonst pro Kachel durch einen XML-Parse.</summary>
        Public Shared Function ReadRotationDegrees(rawPath As String) As Integer
            Return If(ReadDisplayFacts(rawPath)?.Degrees, 0)
        End Function

        ''' <summary>Soll die Kachel dieser Datei ENTWICKELT gezeigt werden, auch wenn die Einstellung
        ''' "Entwickelte RAW-Vorschau" aus ist? Der Vermerk steht als Attribut in der .fpxmp und wird
        ''' gesetzt, wenn der Stapel "Anpassungen anwenden" ein Rezept schreibt: sonst saehe man in
        ''' der Galerie nichts von dem, was man gerade ueber eine Serie gelegt hat.
        '''
        ''' EINMAL GESETZT, BLEIBT ER. Jeder spaetere Schreiber (Editor, Katalog, naechster Stapel)
        ''' uebernimmt ihn, siehe TryWriteCore. Sonst fiele die Kachel nach dem ersten Nachbessern im
        ''' Editor auf das Kamerabild zurueck, obwohl gerade dort mehr Bearbeitung drinsteckt.
        '''
        ''' In der .fpxmp und nicht im Kachelspeicher, weil er mit der Datei wandern muss (Verschieben,
        ''' Umbenennen, Leeren des Caches) und weil die .fpxmp je Kachel ohnehin schon gelesen wird,
        ''' fuer die Drehung. Beide Werte kommen deshalb aus demselben gemerkten Lesevorgang.</summary>
        Public Shared Function ReadDevelopedThumbnail(rawPath As String) As Boolean
            Return If(ReadDisplayFacts(rawPath)?.DevelopedThumbnail, False)
        End Function

        ''' <summary>Wird die Kachel dieser Datei entwickelt gezeigt, auch mit ausgeschalteter
        ''' Einstellung? Ja mit dem Vermerk dafuer (<see cref="ReadDevelopedThumbnail"/>) UND fuer jede
        ''' Datei, deren Begleitdatei sie als bearbeitet fuehrt: wer ein Bild bearbeitet und
        ''' speichert, will die Bearbeitung an der Kachel sehen, nicht die Vorschau der Kamera.
        '''
        ''' Nur der Vermerk "bearbeitet", wie er in der Datei steht. Eine aeltere Begleitdatei ohne ihn
        ''' wird hier nicht mit den Startwerten verglichen: das oeffnete je Kachel die RAW.</summary>
        Public Shared Function ShowsDevelopedThumbnail(rawPath As String) As Boolean
            Dim facts = ReadDisplayFacts(rawPath)
            Return facts IsNot Nothing AndAlso (facts.DevelopedThumbnail OrElse facts.EditedAttribute.GetValueOrDefault())
        End Function

        Private Shared Function ReadDisplayFacts(rawPath As String) As CachedDisplayFacts
            If String.IsNullOrWhiteSpace(rawPath) Then Return Nothing
            Dim sidecar = SidecarPathFor(rawPath)
            Dim stampTicks As Long
            Try
                If Not File.Exists(sidecar) Then Return Nothing
                stampTicks = File.GetLastWriteTimeUtc(sidecar).Ticks
            Catch
                Return Nothing
            End Try

            ' Jede Variante hat ihre eigenen Werte, alle haengen am Zeitstempel der einen Datei.
            Dim factsKey = VariantKey.Make(sidecar, VariantKey.IdOf(rawPath))
            Dim cached As CachedDisplayFacts = Nothing
            If _displayFactsCache.TryGetValue(factsKey, cached) AndAlso cached.StampTicks = stampTicks Then Return cached

            Dim developedThumbnail As Boolean
            Dim editedAttribute As Boolean? = Nothing
            Dim adjustments As ImageAdjustments = Nothing
            ReadRecipeCore(rawPath, adjustments, developedThumbnail, editedAttribute)
            Dim degrees As Integer = 0
            If adjustments IsNot Nothing Then
                ' DIESELBE REGEL WIE IM EDITOR (EditorViewModel.AppliedTransformState): Drehung und
                ' Spiegelung sind nicht vertauschbar. Eine einzelne Spiegelung kehrt den Drehsinn
                ' um, also geht die Drehung eines Schrittes mit umgekehrtem Vorzeichen ein, wenn
                ' vor ihm eine ungerade Zahl von Spiegelungen steht. Blosses Aufsummieren gab
                ' "spiegeln, dann 90 Grad" dieselbe Zahl wie "90 Grad, dann spiegeln", und die
                ' Kachel lag danach quer.
                ' Die oberen Felder zaehlen nicht mehr mit: sie werden beim Laden geraeumt
                ' (FpxService), die Drehung steht ausschliesslich in den Schritten.
                Dim flipH = False, flipV = False
                If adjustments.GeometryOperations IsNot Nothing Then
                    For Each operation In adjustments.GeometryOperations
                        If operation Is Nothing OrElse operation.Adjustments Is Nothing Then Continue For
                        If Not String.Equals(operation.Kind, "transform", StringComparison.OrdinalIgnoreCase) Then Continue For
                        Dim a = operation.Adjustments
                        degrees += If(flipH Xor flipV, -a.RotationDegrees, a.RotationDegrees)
                        flipH = flipH Xor a.FlipHorizontal
                        flipV = flipV Xor a.FlipVertical
                    Next
                End If
                degrees = ImageOrientationService.NormalizeQuarterTurn(degrees)
            End If
            Dim facts = New CachedDisplayFacts(stampTicks, degrees, developedThumbnail, editedAttribute)
            _displayFactsCache(factsKey) = facts
            Return facts
        End Function

        Private NotInheritable Class CachedDisplayFacts
            Public ReadOnly StampTicks As Long
            Public ReadOnly Degrees As Integer
            Public ReadOnly DevelopedThumbnail As Boolean
            ''' Der Vermerk, wie er in der Datei steht; Nothing in einer aelteren Datei ohne ihn.
            Public ReadOnly EditedAttribute As Boolean?
            ''' Ohne Vermerk: das Ergebnis des Vergleichs mit den Startwerten, erst bei der ersten
            ''' Frage danach gerechnet (IsEditedFile). Die Drehung der Kachel braucht ihn nicht, und er
            ''' liest die Metadaten der RAW.
            Public ComparedEdited As Boolean?
            Public Sub New(stampTicks As Long, degrees As Integer, developedThumbnail As Boolean,
                           editedAttribute As Boolean?)
                Me.StampTicks = stampTicks
                Me.Degrees = degrees
                Me.DevelopedThumbnail = developedThumbnail
                Me.EditedAttribute = editedAttribute
            End Sub
        End Class

        Private Shared ReadOnly _displayFactsCache As New ConcurrentDictionary(Of String, CachedDisplayFacts)(PathIdentity.Comparer)

        Private Const DevelopedThumbnailAttribute As String = "developedThumbnail"
        Private Const EditedAttribute As String = "edited"

        ''' <summary>Der Vermerk "bearbeitet", wie er in der Begleitdatei steht; Nothing ohne Datei oder
        ''' in einer aelteren Datei ohne Vermerk.</summary>
        Private Shared Function ReadEditedAttribute(rawPath As String) As Boolean?
            Return ReadDisplayFacts(rawPath)?.EditedAttribute
        End Function

        ''' <summary>Die Startwerte einer Datei ohne Bearbeitung: bei einer RAW die des Editors
        ''' (ImageAdjustments.ForUneditedRaw, mit Grundbelichtung und Kamerakalibrierung), sonst ein
        ''' neutrales Rezept.</summary>
        Public Shared Function StartValuesFor(path As String) As ImageAdjustments
            Dim file = VariantKey.FileOf(path)
            Return If(RawPreviewService.IsSupportedRaw(file), ImageAdjustments.ForUneditedRaw(file), New ImageAdjustments())
        End Function

        ''' <summary>Ist diese Datei bearbeitet? Ein Projektbuendel (.fpx) immer; jedes Rezeptformat
        ''' (RAW, PSD, JPG, PNG, TIFF und die uebrigen aus IsRecipeFormat), wenn seine Begleitdatei den
        ''' Vermerk traegt oder, in einer aelteren Datei ohne ihn, ihr Rezept von den Startwerten
        ''' abweicht. Eine Begleitdatei allein ist KEIN Beleg: bei RAW und PSD entsteht sie schon
        ''' durch einen Stern oder ein Stichwort. Ohne Begleitdatei gilt eine Datei als unbearbeitet,
        ''' auch ein JPG, in dessen Bildpunkte eine Bearbeitung gespeichert wurde. Liest dieselbe
        ''' gemerkte Datei wie die Drehung der Kachel.</summary>
        Public Shared Function IsEditedFile(path As String) As Boolean
            If String.IsNullOrWhiteSpace(path) Then Return False
            If FpxService.IsFpx(path) Then Return True
            If Not IsRecipeFormat(path) Then Return False
            Dim facts = ReadDisplayFacts(path)
            If facts Is Nothing Then Return False
            If facts.EditedAttribute.HasValue Then Return facts.EditedAttribute.Value
            ' Eine aeltere Begleitdatei ohne Vermerk: der nachsichtige Vergleich mit den Startwerten,
            ' gemerkt mit den uebrigen Werten dieses Zeitstempels und NICHT zurueckgeschrieben - im
            ' Bestand schreibt nur ein bewusstes Speichern. Beim naechsten Speichern steht der echte
            ' Vermerk darin.
            If Not facts.ComparedEdited.HasValue Then
                Dim adjustments = TryRead(path)
                facts.ComparedEdited = adjustments IsNot Nothing AndAlso
                                       adjustments.IsEditedComparedTo(StartValuesFor(path), lenient:=True)
            End If
            Return facts.ComparedEdited.Value
        End Function

        ''' <summary>Der Vermerk fuer ein Rezept, das ein Bearbeitungsweg gerade schreibt: weicht es
        ''' von den Startwerten der Datei ab? Streng, die Startwerte sind ja die von jetzt.</summary>
        Public Shared Function IsEditedRecipe(path As String, adjustments As ImageAdjustments) As Boolean
            Return adjustments IsNot Nothing AndAlso adjustments.IsEditedComparedTo(StartValuesFor(path))
        End Function

        ''' <summary>Übernimmt die Entwicklungseinstellungen aus einer XMP-Sidecar
        ''' ("foto.cr2.xmp" oder "foto.xmp") in eine neue .fpxmp - EINMALIG, solange es noch keine gibt.
        '''
        ''' Damit gibt es genau EINEN Ort, an dem crs:-Werte in unser Rezeptformat übersetzt werden: der
        ''' Ordner-Scan, der die Beistelldateien ohnehin schon wegen Bewertung und Stichworten anfasst.
        ''' Editor und Viewer müssen XMP dadurch überhaupt nicht kennen und arbeiten weiter allein auf
        ''' der .fpxmp.
        '''
        ''' Drei Bedingungen, alle nötig: nur für RAW/PSD (eine XMP neben einem JPG wird bewusst nicht
        ''' übernommen: das wäre eine sichtbare Änderung an vielen Bildern auf einmal), nur wenn noch
        ''' keine .fpxmp existiert (ein eigenes Rezept wird NIE überschrieben),
        ''' und nur wenn die Sidecar wirklich Reglerwerte trägt. Letzteres ist wichtig: die allermeisten
        ''' XMP-Beistelldateien enthalten nur Katalogdaten, und eine neutrale .fpxmp neben jedes Foto zu
        ''' legen wäre eine Dateiflut ohne jeden Inhalt.</summary>
        Public Shared Function TryImportFromXmpSidecar(rawPath As String) As Boolean
            If String.IsNullOrWhiteSpace(rawPath) OrElse Not IsReadOnlyFormat(rawPath) Then Return False
            If Exists(rawPath) Then Return False
            Try
                Dim xmpPath = XmpSidecarService.FindSidecar(rawPath)
                If String.IsNullOrEmpty(xmpPath) Then Return False
                ' Der RAW-Pfad geht mit: trägt die Sidecar ein absolutes crs:Temperature ohne
                ' crs:AsShotTemperature, holt der Import die Aufnahmetemperatur aus der RAW-Datei
                ' selbst, statt D65 anzunehmen.
                Dim look = XmpPresetService.LoadLook(xmpPath, imagePath:=rawPath)
                If look Is Nothing OrElse Not HasAnyAdjustment(look) Then Return False
                ' Nur anlegen, wenn noch immer keine .fpxmp da ist: zwischen der Pruefung oben und
                ' hier kann eine entstanden sein, und ein eigenes Rezept wird NIE ueberschrieben.
                ' Bearbeitet: die Uebernahme laeuft nur, wenn die fremde Datei Regler setzt.
                Return TryWrite(rawPath, look, requireAbsent:=True, edited:=True)
            Catch
                Return False
            End Try
        End Function

        ''' <summary>Dasselbe fuer die Entwicklung aus einem Katalog des Referenzprogramms (ForeignCatalogService):
        ''' EINMALIG in eine neue .fpxmp, nur fuer RAW und PSD ohne eigene, und nur fuer ein Foto, das
        ''' das Referenzprogramm selbst als bearbeitet fuehrt. Ohne die letzte Bedingung bekaeme jedes Foto des
        ''' Katalogs eine .fpxmp, denn auch eine unbearbeitete Entwicklung traegt dort Werte (die
        ''' Vorgaben fuer Schaerfe und Rauschminderung). Die Lua-Tabelle wird vorher in ein XMP mit
        ''' denselben Schluesseln gebracht, uebersetzt wird sie dann vom selben Leser wie eine
        ''' XMP-Beistelldatei. Steht beides da, gewinnt die Beistelldatei: sie wird vorher gefragt.</summary>
        Public Shared Function TryImportFromForeignCatalog(rawPath As String) As Boolean
            If String.IsNullOrWhiteSpace(rawPath) OrElse Not IsReadOnlyFormat(rawPath) Then Return False
            If Exists(rawPath) Then Return False
            Try
                Dim entry = ForeignCatalogService.Lookup(rawPath)
                If entry Is Nothing OrElse Not entry.Edited Then Return False
                Dim xmp = ForeignCatalogService.DevelopAsXmp(entry)
                If String.IsNullOrEmpty(xmp) Then Return False
                Dim look = XmpPresetService.LoadLookFromText(xmp, imagePath:=rawPath)
                If look Is Nothing OrElse Not HasAnyAdjustment(look) Then Return False
                Return TryWrite(rawPath, look, requireAbsent:=True, edited:=True)
            Catch ex As Exception
                DiagnosticLogService.LogException("ForeignCatalog.Entwicklung", ex)
                Return False
            End Try
        End Function

        ''' Vergleicht gegen ein frisches ImageAdjustments statt gegen eine handgepflegte Feldliste -
        ''' ein neuer Regler ist damit automatisch dabei und kann nicht vergessen werden.
        Private Shared Function HasAnyAdjustment(look As ImageAdjustments) As Boolean
            Dim neutral As New ImageAdjustments()
            For Each p In ImageAdjustments.PixelAdjustmentProperties()
                If Not Equals(p.GetValue(look), p.GetValue(neutral)) Then Return True
            Next
            Return False
        End Function

        ''' <summary>Was beim Lesen einer .fpxmp herauskam. Fehlend und unlesbar MUESSEN getrennt
        ''' bleiben: nur eine fehlende Datei darf ein Schreiber neu anlegen.</summary>
        Public Enum SidecarReadResult
            Missing
            Read
            ''' Vorhanden, aber nicht verstanden: defektes XML, unbekannte oder neuere Formatversion,
            ''' fehlendes oder unlesbares Rezept, Lesefehler.
            Unreadable
        End Enum

        ''' <summary>Liest das Rezept aus dem Sidecar. Nothing, wenn keiner da ist, die Version
        ''' unbekannt oder die Datei defekt ist - der Editor startet dann wie ohne Sidecar. Wer danach
        ''' SCHREIBEN will, nimmt <see cref="ReadRecipe"/> und unterscheidet die Faelle.</summary>
        Public Shared Function TryRead(rawPath As String) As ImageAdjustments
            Dim developedThumbnail As Boolean
            Return TryReadCore(rawPath, developedThumbnail)
        End Function

        ''' <summary>Wie <see cref="TryRead"/>, sagt aber, ob die Datei fehlt oder nur nicht lesbar ist.</summary>
        Public Shared Function ReadRecipe(rawPath As String, ByRef adjustments As ImageAdjustments) As SidecarReadResult
            Dim developedThumbnail As Boolean
            Return ReadRecipeCore(rawPath, adjustments, developedThumbnail)
        End Function

        Private Shared Function TryReadCore(rawPath As String, ByRef developedThumbnail As Boolean) As ImageAdjustments
            Dim adjustments As ImageAdjustments = Nothing
            ReadRecipeCore(rawPath, adjustments, developedThumbnail)
            Return adjustments
        End Function

        ''' <summary>Gibt es die Datei? Read heisst hier nur "vorhanden".
        '''
        ''' NICHT File.Exists: das sagt auch bei einem Zugriffsfehler False (Netzlaufwerk weg,
        ''' fehlende Rechte am Ordner), und dann sah eine vorhandene Datei wie eine fehlende aus und
        ''' wurde neu angelegt. File.GetAttributes wirft dagegen und sagt mit dem Ausnahmetyp, was
        ''' los ist: Datei oder Ordner nicht gefunden heisst Missing, jeder andere Fehler Unreadable.
        '''
        ''' AUCH KEINE ORDNERAUFZAEHLUNG mit dem Namen als Muster: unter Unix geht die durch den
        ''' ganzen Ordner und filtert erst danach. Ein Stapel ueber tausend Bilder auf einem
        ''' Netzlaufwerk las so tausendmal den ganzen Ordner.
        '''
        ''' Ein ORDNER mit dem Namen der Begleitdatei ist Unreadable: er laesst sich weder lesen
        ''' noch ersetzen, und ihn als "fehlt" zu melden, liesse einen Schreiber gegen ihn laufen.</summary>
        Private Shared Function ProbeSidecar(sidecar As String) As SidecarReadResult
            Try
                Dim attributes = File.GetAttributes(sidecar)
                If (attributes And FileAttributes.Directory) = FileAttributes.Directory Then Return SidecarReadResult.Unreadable
                Return SidecarReadResult.Read
            Catch ex As FileNotFoundException
                Return SidecarReadResult.Missing
            Catch ex As DirectoryNotFoundException
                Return SidecarReadResult.Missing
            Catch
                Return SidecarReadResult.Unreadable
            End Try
        End Function

        Private Shared Function ReadRecipeCore(rawPath As String, ByRef adjustments As ImageAdjustments,
                                               ByRef developedThumbnail As Boolean) As SidecarReadResult
            Dim edited As Boolean? = Nothing
            Return ReadRecipeCore(rawPath, adjustments, developedThumbnail, edited)
        End Function

        Private Shared Function ReadRecipeCore(rawPath As String, ByRef adjustments As ImageAdjustments,
                                               ByRef developedThumbnail As Boolean,
                                               ByRef edited As Boolean?) As SidecarReadResult
            adjustments = Nothing
            developedThumbnail = False
            edited = Nothing
            Dim sidecar = SidecarPathFor(rawPath)
            Select Case ProbeSidecar(sidecar)
                Case SidecarReadResult.Missing
                    Return SidecarReadResult.Missing
                Case SidecarReadResult.Unreadable
                    Return SidecarReadResult.Unreadable
            End Select
            Try
                Dim doc = XDocument.Load(sidecar)
                Dim root = doc.Root
                If root Is Nothing OrElse root.Name <> Ns + "recipe" Then Return SidecarReadResult.Unreadable
                developedThumbnail = String.Equals(root.Attribute(DevelopedThumbnailAttribute)?.Value, "true",
                                                   StringComparison.OrdinalIgnoreCase)
                Dim editedValue = root.Attribute(EditedAttribute)?.Value
                If editedValue IsNot Nothing Then edited = String.Equals(editedValue, "true", StringComparison.OrdinalIgnoreCase)
                Dim version As Integer
                If Not Integer.TryParse(root.Attribute("version")?.Value, Globalization.NumberStyles.Integer,
                                        Globalization.CultureInfo.InvariantCulture, version) Then
                    Return SidecarReadResult.Unreadable
                End If
                If version < 1 OrElse version > VariantsFormatVersion Then Return SidecarReadResult.Unreadable
                ' Ein Variantsblock, den wir nicht verstehen, macht die ganze Datei unlesbar: sonst
                ' schriebe der naechste Schreiber sie ohne ihn zurueck.
                Dim ignoredVariants As VariantSet = Nothing
                If Not TryParseVariants(root, ignoredVariants) Then Return SidecarReadResult.Unreadable
                Dim adjustmentsNode = root.Element(Ns + "adjustments")
                If adjustmentsNode Is Nothing Then Return SidecarReadResult.Unreadable
                Dim variantId As String = Nothing
                Dim file = VariantKey.Split(rawPath, variantId)
                Dim json = adjustmentsNode.Value
                If variantId.Length > 0 Then
                    ' Gemeint ist eine zusaetzliche Variante: ihr Rezept, ihr Vermerk. Gibt es sie
                    ' nicht (mehr), ist das fuer einen Schreiber "nicht anfassen" und fuer einen
                    ' Leser "kein Rezept".
                    Dim entry = ignoredVariants?.Find(variantId)
                    If entry Is Nothing Then Return SidecarReadResult.Unreadable
                    json = entry.Json
                    edited = entry.Edited
                    developedThumbnail = True
                End If
                adjustments = FpxService.DeserializeAdjustments(json)
                ' Ein Entrauschen aus der Zeit der 8-Bit-Modelle geht bei einer RAW in die RAW-Stufe.
                DenoiseModelService.MigrateLegacyRecipe(adjustments, file)
                Return If(adjustments Is Nothing, SidecarReadResult.Unreadable, SidecarReadResult.Read)
            Catch
                adjustments = Nothing
                Return SidecarReadResult.Unreadable
            End Try
        End Function

        ' ---------------------------------------------------------------- Titel und Beschreibung

        ''' <summary>Die Elementnamen der beschreibenden Felder im Block "description". Eigene Namen,
        ''' nicht die von XMP: die Datei ist unser Format, und die Felder stehen flach.</summary>
        Private Shared Function DescriptionElementName(field As ImageDescriptionField) As String
            Select Case field
                Case ImageDescriptionField.Title : Return "title"
                Case ImageDescriptionField.Headline : Return "headline"
                Case ImageDescriptionField.Description : Return "text"
                Case ImageDescriptionField.Creator : Return "creator"
                Case Else : Return "credit"
            End Select
        End Function

        ''' <summary>Der Block "description" mit den belegten Feldern; Nothing, wenn keines belegt
        ''' ist, dann fehlt der Block ganz. Ohne neue Formatversion: aeltere Fassungen lesen ihn nicht
        ''' und schreiben ihn beim naechsten Speichern nicht mit, lehnen die Datei aber nicht ab.</summary>
        Private Shared Function DescriptionNode(description As ImageDescription) As XElement
            If description Is Nothing OrElse description.IsEmpty Then Return Nothing
            Dim node = New XElement(Ns + "description")
            For Each field In ImageDescription.AllFields
                Dim value = description.GetValue(field)
                If value.Length > 0 Then node.Add(New XElement(Ns + DescriptionElementName(field), value))
            Next
            Return node
        End Function

        ''' <summary>Titel, Ueberschrift, Beschreibung, Urheber und Quellenangabe aus der .fpxmp;
        ''' Nothing ohne Datei, ohne Block oder bei einer unlesbaren Datei.
        '''
        ''' WARUM HIER: bei RAW und PSD ist die .fpxmp der eigene, dauerhafte Ort neben dem Bild, wie
        ''' fuer Sterne und Stichwoerter. Ein Titel, der nur in der Katalogspalte stuende, waere beim
        ''' naechsten Einlesen weg, denn dort steht nur die Abschrift der Datei. Gelesen wird sie vor der
        ''' XMP-Beistelldatei (ImageDescriptionService.FromExifData); der Dialog schreibt in beide.</summary>
        Public Shared Function TryReadDescription(rawPath As String) As ImageDescription
            Try
                If String.IsNullOrWhiteSpace(rawPath) OrElse Not IsReadOnlyFormat(rawPath) Then Return Nothing
                Dim sidecar = SidecarPathFor(rawPath)
                If Not File.Exists(sidecar) Then Return Nothing
                Dim root = XDocument.Load(sidecar).Root
                If root Is Nothing OrElse root.Name <> Ns + "recipe" Then Return Nothing
                Dim node = root.Element(Ns + "description")
                If node Is Nothing Then Return Nothing
                Dim result As New ImageDescription()
                For Each field In ImageDescription.AllFields
                    Dim element = node.Element(Ns + DescriptionElementName(field))
                    If element IsNot Nothing Then result.SetValue(field, ImageDescriptionService.NormalizeValue(field, element.Value))
                Next
                Return result
            Catch
                Return Nothing
            End Try
        End Function

        ''' <summary>Setzt die genannten Felder in der .fpxmp, die uebrigen bleiben; ein leerer Wert
        ''' entfernt das Feld. Rezept und Katalogblock bleiben, wie sie sind. Fehlt die Datei, entsteht
        ''' sie wie bei einer Katalogaenderung mit neutralem Rezept; eine unlesbare bleibt unangetastet
        ''' (False).</summary>
        Public Shared Function TryWriteDescription(rawPath As String,
                                                   changes As IReadOnlyDictionary(Of ImageDescriptionField, String)) As Boolean
            If String.IsNullOrWhiteSpace(rawPath) OrElse Not IsReadOnlyFormat(rawPath) OrElse changes Is Nothing Then Return False
            ' Titel und Beschreibung gehoeren der Aufnahme, nicht einer Variante.
            rawPath = VariantKey.FileOf(rawPath)
            Try
                SyncLock _writeLock
                    Using crossLock = LockAcrossProcesses(rawPath)
                        If crossLock Is Nothing Then Return False
                        Dim adjustments As ImageAdjustments = Nothing
                        Dim state = ReadRecipe(rawPath, adjustments)
                        If state = SidecarReadResult.Unreadable Then Return False
                        If state = SidecarReadResult.Missing Then adjustments = New ImageAdjustments()
                        Dim merged = If(TryReadDescription(rawPath), New ImageDescription())
                        For Each pair In changes
                            merged.SetValue(pair.Key, ImageDescriptionService.NormalizeValue(pair.Key, pair.Value))
                        Next
                        Dim catalog = LibraryService.Instance.GetSidecarCatalogData(rawPath)
                        ' Ein leerer Block muss ausdruecklich als leer hinein, sonst behielte TryWriteCore
                        ' den alten aus der Datei.
                        Return TryWriteCore(rawPath, adjustments, catalog, Nothing, Nothing,
                                            requireAbsent:=state = SidecarReadResult.Missing,
                                            description:=merged)
                    End Using
                End SyncLock
            Catch ex As Exception
                DiagnosticLogService.LogException("Sidecar.WriteDescription", ex)
                Return False
            End Try
        End Function

        ''' <summary>Titel und Co. aus einem Katalog des Referenzprogramms (ForeignCatalogService.DescriptionFor)
        ''' in die .fpxmp, nur fuer RAW und PSD und nur in Felder, die an der Datei noch leer sind
        ''' (.fpxmp, XMP-Beistelldatei und Eingebettetes zusammen). Andere Formate bekommen nichts:
        ''' ein Katalogimport schreibt keine Bilddatei um und legt keine XMP an.</summary>
        Public Shared Function TryImportDescriptionFromForeignCatalog(rawPath As String) As Boolean
            If String.IsNullOrWhiteSpace(rawPath) OrElse Not IsReadOnlyFormat(rawPath) Then Return False
            Try
                Dim entry = ForeignCatalogService.Lookup(rawPath)
                If entry Is Nothing Then Return False
                Dim incoming = ForeignCatalogService.DescriptionFor(entry)
                If incoming Is Nothing OrElse incoming.IsEmpty Then Return False
                Dim current = ImageDescriptionService.Read(rawPath)
                Dim changes As New Dictionary(Of ImageDescriptionField, String)()
                For Each field In ImageDescription.AllFields
                    If current.GetValue(field).Length = 0 AndAlso incoming.GetValue(field).Length > 0 Then
                        changes(field) = incoming.GetValue(field)
                    End If
                Next
                If changes.Count = 0 Then Return False
                If Not TryWriteDescription(rawPath, changes) Then Return False
                ExifService.Invalidate(rawPath)
                Return True
            Catch ex As Exception
                DiagnosticLogService.LogException("ForeignCatalog.Beschreibung", ex)
                Return False
            End Try
        End Function

        ''' <summary>Das Format der Zeiten im Block: Uhrzeit vor Ort, ohne Zone (die steht daneben).</summary>
        Private Const ImportedTimeFormat As String = "yyyy-MM-ddTHH:mm:ss"

        ''' <summary>Der Block mit dem, was aus dem Katalog des Referenzprogramms uebernommen wurde
        ''' (Copyright, Ort, eine dort geaenderte Aufnahmezeit), oder Nothing ohne Werte. Eine ABSCHRIFT
        ''' jenes Katalogs, keine eigene Angabe: gelesen wird er nach Datei und XMP-Beistelldatei
        ''' (ExifService.ApplyImportedMetadata), der Dialog schreibt nie hinein.</summary>
        Private Shared Function ImportedNode(value As ForeignCatalogService.ImportedMetadata) As XElement
            If value Is Nothing OrElse value.IsEmpty Then Return Nothing
            Dim invariant = Globalization.CultureInfo.InvariantCulture
            Dim node = New XElement(Ns + "imported", New XAttribute("source", "lrcat"))
            If value.Copyright.Length > 0 Then node.Add(New XElement(Ns + "copyright", value.Copyright))
            If value.HasCoordinates Then
                node.Add(New XElement(Ns + "latitude", value.Latitude.Value.ToString("R", invariant)))
                node.Add(New XElement(Ns + "longitude", value.Longitude.Value.ToString("R", invariant)))
            End If
            If value.City.Length > 0 Then node.Add(New XElement(Ns + "city", value.City))
            If value.Country.Length > 0 Then node.Add(New XElement(Ns + "country", value.Country))
            If value.HasEditedCaptureTime Then
                node.Add(New XElement(Ns + "captureTime", value.CaptureTime.Value.ToString(ImportedTimeFormat, invariant)))
                node.Add(New XElement(Ns + "originalCaptureTime", value.OriginalCaptureTime.Value.ToString(ImportedTimeFormat, invariant)))
                If value.CaptureOffset.Length > 0 Then node.Add(New XElement(Ns + "captureOffset", value.CaptureOffset))
            End If
            Return node
        End Function

        ''' <summary>Was aus dem Katalog des Referenzprogramms uebernommen wurde, aus der .fpxmp; Nothing
        ''' ohne Datei, ohne Block oder bei einer unlesbaren Datei.
        '''
        ''' WARUM HIER: bei RAW und PSD bleibt es so erhalten, auch wenn der fremde Katalog spaeter
        ''' nicht mehr eingestellt ist oder fehlt. Andere Formate bekommen keinen Block; dort legt
        ''' ExifService die Werte beim Lesen aus dem Katalog selbst dazu.</summary>
        Public Shared Function TryReadImported(rawPath As String) As ForeignCatalogService.ImportedMetadata
            Try
                If String.IsNullOrWhiteSpace(rawPath) OrElse Not IsReadOnlyFormat(rawPath) Then Return Nothing
                Dim sidecar = SidecarPathFor(rawPath)
                If Not File.Exists(sidecar) Then Return Nothing
                Dim root = XDocument.Load(sidecar).Root
                If root Is Nothing OrElse root.Name <> Ns + "recipe" Then Return Nothing
                Dim node = root.Element(Ns + "imported")
                If node Is Nothing Then Return Nothing
                Dim invariant = Globalization.CultureInfo.InvariantCulture
                Dim result As New ForeignCatalogService.ImportedMetadata With {
                    .Copyright = If(node.Element(Ns + "copyright")?.Value, "").Trim(),
                    .City = If(node.Element(Ns + "city")?.Value, "").Trim(),
                    .Country = If(node.Element(Ns + "country")?.Value, "").Trim()}
                Dim captured, original As DateTime
                If DateTime.TryParseExact(node.Element(Ns + "captureTime")?.Value, ImportedTimeFormat, invariant, Globalization.DateTimeStyles.None, captured) AndAlso
                   DateTime.TryParseExact(node.Element(Ns + "originalCaptureTime")?.Value, ImportedTimeFormat, invariant, Globalization.DateTimeStyles.None, original) Then
                    result.CaptureTime = captured
                    result.OriginalCaptureTime = original
                    result.CaptureOffset = ExifService.NormalizeOffset(node.Element(Ns + "captureOffset")?.Value)
                End If
                Dim latitude, longitude As Double
                If Double.TryParse(node.Element(Ns + "latitude")?.Value, Globalization.NumberStyles.Float, invariant, latitude) AndAlso
                   Double.TryParse(node.Element(Ns + "longitude")?.Value, Globalization.NumberStyles.Float, invariant, longitude) AndAlso
                   Math.Abs(latitude) <= 90 AndAlso Math.Abs(longitude) <= 180 Then
                    result.Latitude = latitude
                    result.Longitude = longitude
                End If
                Return If(result.IsEmpty, Nothing, result)
            Catch
                Return Nothing
            End Try
        End Function

        ''' <summary>Copyright, Ort und eine geaenderte Aufnahmezeit aus dem Katalog des
        ''' Referenzprogramms in die .fpxmp, nur fuer RAW und PSD. Der Block ist eine Abschrift: er wird
        ''' ERSETZT, sobald der Katalog etwas anderes sagt, damit eine spaetere Aenderung dort ankommt.
        ''' Ob ein Wert gilt, entscheidet erst das Lesen (Datei und XMP-Beistelldatei gehen vor, die
        ''' Zeit nach ForeignCatalogService.EditedCaptureTime), deshalb wird hier nichts gegen die Datei
        ''' abgeglichen. Sagt der Katalog nichts (mehr), bleibt ein vorhandener Block stehen.
        ''' Fehlt die .fpxmp, entsteht sie wie beim Titel mit neutralem Rezept.</summary>
        Public Shared Function TryImportMetadataFromForeignCatalog(rawPath As String) As Boolean
            If String.IsNullOrWhiteSpace(rawPath) OrElse Not IsReadOnlyFormat(rawPath) Then Return False
            rawPath = VariantKey.FileOf(rawPath)
            Try
                Dim entry = ForeignCatalogService.Lookup(rawPath)
                If entry Is Nothing Then Return False
                Dim incoming = ForeignCatalogService.ImportedMetadataFor(entry)
                If incoming Is Nothing OrElse incoming.IsEmpty Then Return False
                If incoming.SameAs(TryReadImported(rawPath)) Then Return False
                SyncLock _writeLock
                    Using crossLock = LockAcrossProcesses(rawPath)
                        If crossLock Is Nothing Then Return False
                        Dim adjustments As ImageAdjustments = Nothing
                        Dim state = ReadRecipe(rawPath, adjustments)
                        If state = SidecarReadResult.Unreadable Then Return False
                        If state = SidecarReadResult.Missing Then adjustments = New ImageAdjustments()
                        Dim catalog = LibraryService.Instance.GetSidecarCatalogData(rawPath)
                        If Not TryWriteCore(rawPath, adjustments, catalog, Nothing, Nothing,
                                            requireAbsent:=state = SidecarReadResult.Missing,
                                            imported:=incoming) Then Return False
                    End Using
                End SyncLock
                ExifService.Invalidate(rawPath)
                Return True
            Catch ex As Exception
                DiagnosticLogService.LogException("ForeignCatalog.Ort", ex)
                Return False
            End Try
        End Function

        ''' <summary>Liest nur den Katalogblock. Nothing bedeutet: keine Sidecar, alte Version-1-Datei
        ''' ohne Katalogdaten oder defekte Datei. Ein vorhandener Block liefert auch bewusst leere
        ''' Werte (Rating 0, Favorit False, keine Stichwoerter).</summary>
        Public Shared Function TryReadCatalog(rawPath As String) As RawSidecarCatalogData
            Try
                Dim sidecar = SidecarPathFor(rawPath)
                If Not File.Exists(sidecar) Then Return Nothing
                Return CatalogFromRoot(XDocument.Load(sidecar).Root, rawPath)
            Catch
                Return Nothing
            End Try
        End Function

        ''' <summary>Der Katalogblock des Schluessels aus einem schon gelesenen Dokument, nach den
        ''' Regeln von <see cref="TryReadCatalog"/>. Die Formatversion wird ohne Umwandlung gelesen:
        ''' eine Zahl jenseits von Integer oder ein Wort darin heisst "nicht verstanden", kein
        ''' Ueberlauf.</summary>
        Private Shared Function CatalogFromRoot(root As XElement, rawPath As String) As RawSidecarCatalogData
            Try
                If root Is Nothing OrElse root.Name <> Ns + "recipe" Then Return Nothing
                Dim invariant = Globalization.CultureInfo.InvariantCulture
                Dim version As Integer
                If Not Integer.TryParse(root.Attribute("version")?.Value, Globalization.NumberStyles.Integer, invariant, version) Then Return Nothing
                If version < 1 OrElse version > VariantsFormatVersion Then Return Nothing
                Dim catalogNode = root.Element(Ns + "catalog")
                Dim variantId = VariantKey.IdOf(rawPath)
                If variantId.Length > 0 Then
                    Dim variants As VariantSet = Nothing
                    If Not TryParseVariants(root, variants) OrElse variants Is Nothing Then Return Nothing
                    catalogNode = variants.Find(variantId)?.Catalog
                End If
                If catalogNode Is Nothing Then Return Nothing

                Dim result As New RawSidecarCatalogData()
                Dim ratingNode = catalogNode.Element(Ns + "rating")
                Dim rating As Integer
                If ratingNode IsNot Nothing AndAlso Integer.TryParse(ratingNode.Value, Globalization.NumberStyles.Integer, invariant, rating) Then
                    result.Rating = Math.Max(0, Math.Min(5, rating))
                End If
                Dim favoriteNode = catalogNode.Element(Ns + "favorite")
                Dim favorite As Boolean
                If favoriteNode IsNot Nothing AndAlso Boolean.TryParse(favoriteNode.Value, favorite) Then
                    result.IsFavorite = favorite
                End If
                Dim colorLabelNode = catalogNode.Element(Ns + "colorLabel")
                If colorLabelNode IsNot Nothing Then result.ColorLabel = colorLabelNode.Value.Trim()
                Dim pickNode = catalogNode.Element(Ns + "pick")
                Dim pick As Integer
                If pickNode IsNot Nothing AndAlso Integer.TryParse(pickNode.Value, Globalization.NumberStyles.Integer, invariant, pick) Then
                    result.PickState = LibraryService.NormalizePickState(pick)
                End If
                Dim keywordsNode = catalogNode.Element(Ns + "keywords")
                If keywordsNode IsNot Nothing Then
                    result.HasKeywords = True
                    result.Keywords = NormalizeKeywords(keywordsNode.Elements(Ns + "keyword").Select(Function(node) node.Value))
                End If
                Return result
            Catch
                Return Nothing
            End Try
        End Function

    End Class

End Namespace
