Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Text.Json

Namespace Services

    ''' <summary>Liest die JSON-Beistelldatei eines anderen RAW-Entwicklers (".rrdata" neben dem Bild,
    ''' "foto.cr3.rrdata") fuer den Katalog: Bewertung, Markierung, Farbetikett und Stichwoerter.
    '''
    ''' WARUM: Dieser Entwickler fuehrt keinen Katalog. Was dort bewertet, markiert und
    ''' verschlagwortet wird, steht nur in dieser Datei; in eine .xmp schreibt er es erst mit einem
    ''' eigens eingeschalteten Abgleich. Ein Sichtungsprogramm, das dieselbe Datei beschreibt, legt
    ''' seine Angaben ebenfalls dort ab.
    '''
    ''' NUR LESEN, und nur mit eingeschalteter Einstellung (<see cref="AppSettings.ReadPp3Sidecars"/>,
    ''' derselbe Schalter wie fuer die .pp3, ab Werk aus). FerrumPix schreibt nie hinein und legt nie
    ''' eine an. Die Bearbeitung darin ("adjustments": Regler, Kurven, Masken) wird nicht ausgewertet,
    ''' und virtuelle Kopien ("foto.cr3.a1b2c3.rrdata", sechs Hexziffern vor der Endung) bleiben beim
    ''' Lesen aussen vor: gelesen wird nur die Datei des Originals.
    '''
    ''' MITWANDERN unabhaengig vom Schalter: Umbenennen, Verschieben, Kopieren und Loeschen eines
    ''' Bildes nehmen die Datei des Originals UND die virtuellen Kopien mit
    ''' (RawSidecarService.AccompanyMove und Geschwister, <see cref="BelongsTo"/>). Bewegt wird nur
    ''' die Datei, ihr Inhalt bleibt bytegleich.
    '''
    ''' DAS FORMAT, soweit es hier gebraucht wird (ein JSON-Objekt, unbekannte Felder werden
    ''' uebergangen):
    ''' - "rating": ganze Zahl, 0 heisst unbewertet; gezaehlt werden 1 bis 5.
    ''' - "flag": "pick" oder "reject", sonst fehlt das Feld oder steht auf null. Das
    '''   Sichtungsprogramm nimmt auch 1 und -1 an. Aeltere Dateien tragen die Markierung als
    '''   Stichwort "rr:pick" bzw. "rr:reject"; das gilt nur, wenn das Feld "flag" ganz fehlt.
    ''' - "tags": Liste von Texten mit Vorsilbe. "color:red" ist das Farbetikett (rot, gelb, gruen,
    '''   blau, lila; steht mehr als eines da, gilt das letzte), "user:Wort" ist ein von Hand
    '''   vergebenes Stichwort. Ohne Vorsilbe stehen dort die Schlagwoerter der automatischen
    '''   Bilderkennung des Entwicklers, "ai:" sind dieselben aus dem Sichtungsprogramm; beides
    '''   wird NICHT uebernommen, wie die eigenen KI-Stichwoerter von Handarbeit getrennt bleiben.
    ''' - "color": das Farbetikett als Wort im Wurzelobjekt, so schreibt es das Sichtungsprogramm.
    '''   Es gilt nur, wenn kein "color:"-Stichwort dasteht.</summary>
    Public NotInheritable Class RrdataSidecarService

        Public Const Extension As String = ".rrdata"

        ''' <summary>Groesser wird eine solche Datei nur durch eingebettete Bildflicken der Retusche;
        ''' was darueber liegt, ist keine Beistelldatei und wird nicht gelesen.</summary>
        Private Const MaxFileBytes As Long = 64L * 1024L * 1024L

        Private Const ColorPrefix As String = "color:"
        Private Const UserPrefix As String = "user:"
        Private Const LegacyPickTag As String = "rr:pick"
        Private Const LegacyRejectTag As String = "rr:reject"

        Private Shared ReadOnly ParseOptions As New JsonDocumentOptions With {
            .AllowTrailingCommas = True,
            .CommentHandling = JsonCommentHandling.Skip,
            .MaxDepth = 512
        }

        Private Sub New()
        End Sub

        ''' <summary>Der Pfad, unter dem die Datei des Originals laege: der volle Bildname mit
        ''' ".rrdata" dahinter.</summary>
        Public Shared Function PathFor(imagePath As String) As String
            If String.IsNullOrWhiteSpace(imagePath) Then Return ""
            Return imagePath & Extension
        End Function

        ''' <summary>Gehoert diese Datei zum Bild? Das ist die Datei des Originals ("foto.cr3.rrdata")
        ''' und jede virtuelle Kopie ("foto.cr3.a1b2c3.rrdata": ein Punkt und sechs Hexziffern
        ''' zwischen Bildname und Endung). Die Endung zaehlt klein geschrieben, wie der Entwickler sie
        ''' anlegt und wie die Ordnerauflistung sie sucht.</summary>
        Public Shared Function BelongsTo(filePath As String, imagePath As String) As Boolean
            If String.IsNullOrEmpty(filePath) OrElse String.IsNullOrWhiteSpace(imagePath) Then Return False
            If Not filePath.EndsWith(Extension, StringComparison.Ordinal) Then Return False
            If filePath.Length < imagePath.Length + Extension.Length Then Return False
            If Not filePath.StartsWith(imagePath, PathIdentity.Comparison) Then Return False
            Dim middle = filePath.Substring(imagePath.Length, filePath.Length - imagePath.Length - Extension.Length)
            If middle.Length = 0 Then Return True
            Return middle.Length = 7 AndAlso middle(0) = "."c AndAlso middle.Skip(1).All(AddressOf Uri.IsHexDigit)
        End Function

        ''' <summary>Die Dateien aus <paramref name="folderFiles"/>, die zum Bild gehoeren
        ''' (<see cref="BelongsTo"/>), die des Originals zuerst.</summary>
        Public Shared Function ExistingFor(imagePath As String, folderFiles As IEnumerable(Of String)) As List(Of String)
            If folderFiles Is Nothing Then Return New List(Of String)()
            Return folderFiles.Where(Function(f) BelongsTo(f, imagePath)).
                OrderBy(Function(f) f.Length).ThenBy(Function(f) f, StringComparer.Ordinal).ToList()
        End Function

        ''' <summary>Der Name, den die Datei neben dem Bild unter seinem neuen Pfad traegt: der Teil
        ''' hinter dem alten Bildnamen (".rrdata" oder ".a1b2c3.rrdata") bleibt, wie er war.</summary>
        Public Shared Function RenamedFor(companionPath As String, imagePath As String, newImagePath As String) As String
            Return newImagePath & companionPath.Substring(imagePath.Length)
        End Function

        ''' <summary>Die Daten zur Datei, oder Nothing: Einstellung aus, keine Datei, nicht lesbar,
        ''' kein JSON-Objekt. Die Stufen eines Stichwortpfads ("Orte|Berlin") werden wie beim XMP-Weg
        ''' zur Ordnung der Stichwoerter, am Bild steht das Blatt.</summary>
        Public Shared Function Read(imagePath As String) As Pp3SidecarService.Pp3Data
            If Not Pp3SidecarService.IsEnabled Then Return Nothing
            Dim path = PathFor(imagePath)
            If String.IsNullOrEmpty(path) Then Return Nothing
            Try
                Dim info As New FileInfo(path)
                If Not info.Exists OrElse info.Length > MaxFileBytes Then Return Nothing
                Dim keywordPaths As New List(Of String)()
                Dim result = ParseCore(File.ReadAllText(path, System.Text.Encoding.UTF8), keywordPaths)
                If result Is Nothing Then Return Nothing
                For Each keywordPath In keywordPaths
                    LibraryService.Instance.LearnKeywordPath(keywordPath)
                Next
                Return result
            Catch ex As Exception
                DiagnosticLogService.LogException("Rrdata.Read", ex)
                Return Nothing
            End Try
        End Function

        ''' <summary>Der Anteil fuer den Frische-Stempel der Beistelldateien, oder leer; leer auch bei
        ''' ausgeschalteter Einstellung, wie bei der .pp3. So aendert ein Umschalten den Stempel, und
        ''' Galerie und Katalogindex lesen die Datei neu ein. <see cref="FolderSidecarStamps"/> bildet
        ''' denselben Text aus seiner Auflistung.</summary>
        Public Shared Function StampFor(imagePath As String) As String
            If Not Pp3SidecarService.IsEnabled Then Return ""
            Dim path = PathFor(imagePath)
            Try
                If String.IsNullOrEmpty(path) OrElse Not File.Exists(path) Then Return ""
                Return File.GetLastWriteTime(path).ToString("o")
            Catch
                Return ""
            End Try
        End Function

        ''' <summary>Zerlegt den Text einer .rrdata. Nothing, wenn er kein JSON-Objekt ist.</summary>
        Public Shared Function Parse(text As String) As Pp3SidecarService.Pp3Data
            Return ParseCore(text, Nothing)
        End Function

        Private Shared Function ParseCore(text As String, keywordPaths As List(Of String)) As Pp3SidecarService.Pp3Data
            If String.IsNullOrWhiteSpace(text) Then Return Nothing
            Dim document As JsonDocument
            Try
                document = JsonDocument.Parse(text, ParseOptions)
            Catch ex As JsonException
                DiagnosticLogService.LogException("Rrdata.Parse", ex)
                Return Nothing
            End Try
            Using document
                Dim root = document.RootElement
                If root.ValueKind <> JsonValueKind.Object Then Return Nothing
                Dim result As New Pp3SidecarService.Pp3Data()

                Dim ratingElement As JsonElement
                If root.TryGetProperty("rating", ratingElement) AndAlso ratingElement.ValueKind = JsonValueKind.Number Then
                    Dim rating As Integer
                    ' 0 heisst unbewertet; das fuellt nichts.
                    If ratingElement.TryGetInt32(rating) AndAlso rating >= 1 AndAlso rating <= 5 Then result.Rating = rating
                End If

                Dim flagElement As JsonElement
                Dim hasFlagKey = root.TryGetProperty("flag", flagElement)
                If hasFlagKey Then result.PickState = PickFromFlag(flagElement)

                Dim tagLabel = ""
                Dim tagsElement As JsonElement
                If root.TryGetProperty("tags", tagsElement) AndAlso tagsElement.ValueKind = JsonValueKind.Array Then
                    For Each item In tagsElement.EnumerateArray()
                        If item.ValueKind <> JsonValueKind.String Then Continue For
                        Dim tag = If(item.GetString(), "").Trim()
                        If tag.Length = 0 Then Continue For
                        If tag.StartsWith(ColorPrefix, StringComparison.OrdinalIgnoreCase) Then
                            ' Das letzte Etikett gilt, wie beim Abgleich des Entwicklers nach XMP.
                            Dim mapped = XmpSidecarService.MapLabel(tag.Substring(ColorPrefix.Length))
                            If mapped.Length > 0 Then tagLabel = mapped
                        ElseIf tag.StartsWith(UserPrefix, StringComparison.OrdinalIgnoreCase) Then
                            Dim value = tag.Substring(UserPrefix.Length)
                            Dim leaf = LibraryService.NormalizeKeyword(value)
                            If leaf.Length = 0 Then Continue For
                            If keywordPaths IsNot Nothing AndAlso value.IndexOf("|"c) >= 0 Then keywordPaths.Add(value)
                            If Not result.Keywords.Contains(leaf, StringComparer.OrdinalIgnoreCase) Then result.Keywords.Add(leaf)
                        ElseIf Not hasFlagKey AndAlso String.Equals(tag, LegacyPickTag, StringComparison.OrdinalIgnoreCase) Then
                            result.PickState = 1
                        ElseIf Not hasFlagKey AndAlso String.Equals(tag, LegacyRejectTag, StringComparison.OrdinalIgnoreCase) Then
                            result.PickState = -1
                        End If
                        ' Alles andere sind Schlagwoerter einer automatischen Bilderkennung.
                    Next
                End If

                If tagLabel.Length > 0 Then
                    result.ColorLabel = tagLabel
                Else
                    Dim colorElement As JsonElement
                    If root.TryGetProperty("color", colorElement) AndAlso colorElement.ValueKind = JsonValueKind.String Then
                        result.ColorLabel = XmpSidecarService.MapLabel(colorElement.GetString())
                    End If
                End If
                Return result
            End Using
        End Function

        ''' <summary>1 behalten, -1 verworfen, sonst Nothing.</summary>
        Private Shared Function PickFromFlag(element As JsonElement) As Integer?
            Select Case element.ValueKind
                Case JsonValueKind.String
                    Select Case If(element.GetString(), "").Trim().ToLowerInvariant()
                        Case "pick", "picked" : Return 1
                        Case "reject", "rejected" : Return -1
                        Case Else : Return Nothing
                    End Select
                Case JsonValueKind.Number
                    Dim number As Integer
                    If element.TryGetInt32(number) AndAlso (number = 1 OrElse number = -1) Then Return number
                    Return Nothing
                Case Else
                    Return Nothing
            End Select
        End Function

    End Class

End Namespace
