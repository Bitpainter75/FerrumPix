Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Text.Json

Namespace Services

    ''' Eine einzelne erweiterte Suchbedingung (Bilddaten/EXIF), z.B. "Breite > 1920". Mehrere
    ''' Bedingungen in SearchListEntry.Conditions werden per ConditionCombinator (AND/OR) verknüpft -
    ''' zusätzlich zu (nicht anstelle von) den bestehenden Basisfiltern Text/Favorit/Bewertung,
    ''' die weiterhin immer per AND angewendet werden.
    Public Class SearchCondition
        Public Property Field As String = "Width"
        Public Property [Operator] As String = ">"
        Public Property Value As String = ""

        ''' <summary>"Person" und "Place" stehen bewusst in derselben Liste wie Kamera und ISO, statt
        ''' einen eigenen Filterzustand daneben zu bekommen. Nur so lassen sie sich mit allem anderen
        ''' VERUNDEN - "diese Person, an diesem Ort, mit diesem Stichwort, fuenf Sterne" ist genau
        ''' eine Suchliste mit vier Bedingungen und kein Sonderweg.
        '''
        ''' UNTERSCHIED ZU DEN UEBRIGEN: Ort steht als Text in ImageMeta und wird wie Kamera
        ''' verglichen. Person NICHT - die Zuordnung liegt in eigenen Tabellen, weil ein Bild mehrere
        ''' Personen traegt. Der Vergleich laeuft deshalb ueber eine vorab geholte Zuordnung
        ''' (siehe GalleryViewModel.MatchesCondition), nicht ueber ein Feld in der Zeile.</summary>
        Public Shared ReadOnly ValidFields As String() = {"Width", "Height", "Camera", "Iso", "Aperture", "FocalLength", "DateTaken", "Person", "Place"}
        Public Shared ReadOnly ValidOperators As String() = {">", "<", ">=", "<=", "=", "Contains"}

        ''' <summary>Der Anzeigetext zu einem Feldnamen.
        '''
        ''' Der NAME bleibt, wie er ist: er steht in jeder gespeicherten Suche auf der Platte und in
        ''' jedem Select Case. Uebersetzt wird allein, was im Auswahlfeld steht - sonst passte der
        ''' Vergleich nach dem ersten Sprachwechsel nicht mehr. Unbekanntes kommt unveraendert
        ''' zurueck, damit eine aeltere Suche mit einem Feld, das es nicht mehr gibt, wenigstens
        ''' lesbar bleibt.</summary>
        Public Shared Function FieldLabel(field As String) As String
            Select Case If(field, "").Trim()
                Case "Width" : Return LocalizationService.T("Bildbreite")
                Case "Height" : Return LocalizationService.T("Bildhöhe")
                Case "Camera" : Return LocalizationService.T("Kamera")
                Case "Iso" : Return "ISO"
                Case "Aperture" : Return LocalizationService.T("Blende")
                Case "FocalLength" : Return LocalizationService.T("Brennweite")
                Case "DateTaken" : Return LocalizationService.T("Aufnahmedatum")
                Case "Person" : Return LocalizationService.T("Person")
                Case "Place" : Return LocalizationService.T("Ort")
                Case Else : Return If(field, "")
            End Select
        End Function

        ''' <summary>Der Anzeigetext zu einem Vergleich. Nur "Contains" ist ein Wort und gehoert
        ''' uebersetzt; die Zeichen sprechen fuer sich.</summary>
        Public Shared Function OperatorLabel(op As String) As String
            If String.Equals(If(op, "").Trim(), "Contains", StringComparison.Ordinal) Then
                Return LocalizationService.T("enthält")
            End If
            Return If(op, "")
        End Function
    End Class

    Public Class SearchListEntry
        Public Property Id As String = Guid.NewGuid().ToString("N")
        Public Property Name As String = ""
        ''' "Local" (Dateisystem-Scan), "Immich" oder "Nextcloud" (jeweils Server-Suche). Die Quelle
        ''' wird NICHT im Dialog gewaehlt, sondern ergibt sich aus dem Bereich, in dem "Neue Suche"
        ''' angeklickt wurde - siehe NormalizeSource.
        Public Property Source As String = "Local"
        Public Property TextQuery As String = ""
        Public Property RootFolder As String = ""
        Public Property IncludeSubfolders As Boolean = True
        Public Property FavoriteMode As String = "Any"
        Public Property RatingMin As Integer = -1
        Public Property Ratings As New List(Of Integer)()
        Public Property Results As New List(Of String)()
        Public Property Conditions As New List(Of SearchCondition)()
        Public Property ConditionCombinator As String = "AND"
    End Class

    Public NotInheritable Class SearchListService
        Private Sub New()
        End Sub

        ''' <summary>Bringt eine Quellenangabe auf einen der drei bekannten Werte. Alles Unbekannte
        ''' wird zu "Local": eine Suche, deren Quelle keiner kennt, waere sonst nicht ausfuehrbar.</summary>
        Public Shared Function NormalizeSource(value As String) As String
            If String.Equals(value, "Immich", StringComparison.OrdinalIgnoreCase) Then Return "Immich"
            If String.Equals(value, "Nextcloud", StringComparison.OrdinalIgnoreCase) Then Return "Nextcloud"
            Return "Local"
        End Function

        Private Shared ReadOnly SearchListsDirectory As String =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FerrumPix")

        Private Shared ReadOnly SearchListsPath As String =
            Path.Combine(SearchListsDirectory, "searchlists.json")

        Private Shared ReadOnly JsonOptions As New JsonSerializerOptions With {.WriteIndented = True}

        Public Shared Function Load() As List(Of SearchListEntry)
            Dim entries As List(Of SearchListEntry) = Nothing
            TryLoad(entries)
            Return entries
        End Function

        ''' <summary>
        ''' Liest die Suchlisten und sagt, OB das gelungen ist. Siehe FavoritesService.TryLoad:
        ''' nach einem misslungenen Lesen darf nicht zurueckgeschrieben werden, sonst ersetzt die
        ''' naechste Aenderung den gesamten Bestand.
        '''
        ''' Liefert immer eine benutzbare Liste, aber False, wenn die Datei da war und nicht
        ''' gelesen werden konnte. Eine fehlende Datei ist KEIN Fehler.
        ''' </summary>
        Private Shared Function TryLoad(ByRef entries As List(Of SearchListEntry)) As Boolean
            entries = New List(Of SearchListEntry)()
            Try
                If Not File.Exists(SearchListsPath) Then Return True
                entries = Normalize(JsonSerializer.Deserialize(Of List(Of SearchListEntry))(File.ReadAllText(SearchListsPath)))
                Return True
            Catch ex As JsonException
                JsonStoreService.BackupUnreadable(SearchListsPath, "SearchLists")
                entries = New List(Of SearchListEntry)()
                Return False
            Catch ex As Exception
                DiagnosticLogService.LogException("SearchLists.Load", ex)
                entries = New List(Of SearchListEntry)()
                Return False
            End Try
        End Function

        Public Shared Sub Save(searchLists As IEnumerable(Of SearchListEntry))
            JsonStoreService.WriteAtomic(SearchListsPath,
                                         JsonSerializer.Serialize(Normalize(searchLists?.ToList()), JsonOptions),
                                         "SearchLists")
        End Sub

        Public Shared Sub Delete(searchListId As String)
            Dim geladen As List(Of SearchListEntry) = Nothing
            If Not TryLoad(geladen) Then Return
            Dim entries = geladen.
                Where(Function(s) Not String.Equals(s.Id, searchListId, StringComparison.OrdinalIgnoreCase)).
                ToList()
            Save(entries)
        End Sub

        Public Shared Function Normalize(value As List(Of SearchListEntry)) As List(Of SearchListEntry)
            Dim result As New List(Of SearchListEntry)()
            For Each item In If(value, New List(Of SearchListEntry)())
                If item Is Nothing Then Continue For
                Dim name = If(item.Name, "").Trim()
                If String.IsNullOrWhiteSpace(name) Then name = If(item.TextQuery, "").Trim()
                If String.IsNullOrWhiteSpace(name) Then name = "Suchliste"

                result.Add(New SearchListEntry With {
                    .Id = If(String.IsNullOrWhiteSpace(item.Id), Guid.NewGuid().ToString("N"), item.Id),
                    .Name = name,
                    .Source = NormalizeSource(item.Source),
                    .TextQuery = If(item.TextQuery, "").Trim(),
                    .RootFolder = If(item.RootFolder, "").Trim(),
                    .IncludeSubfolders = item.IncludeSubfolders,
                    .FavoriteMode = AppSettingsService.NormalizeSearchFavoriteMode(item.FavoriteMode),
                    .RatingMin = Math.Max(-1, Math.Min(5, item.RatingMin)),
                    .Ratings = If(item.Ratings, New List(Of Integer)()).
                        Select(Function(r) Math.Max(0, Math.Min(5, r))).
                        Distinct().
                        OrderBy(Function(r) r).
                        ToList(),
                    .Results = NormalizeResults(item.Results),
                    .Conditions = NormalizeConditions(item.Conditions),
                    .ConditionCombinator = If(String.Equals(item.ConditionCombinator, "OR", StringComparison.OrdinalIgnoreCase), "OR", "AND")
                })
            Next
            Return result
        End Function

        ''' <summary>Die gemerkten Treffer einer Suchliste.
        '''
        ''' WAS IM PAPIERKORB LIEGT, BLEIBT AUCH NICHT GEMERKT. Die Trefferliste steht auf der
        ''' Platte, und was dort einmal steht, kommt bei jedem Oeffnen wieder - der Suchlauf selbst
        ''' findet es laengst nicht mehr, und die Datei EXISTIERT ja, faellt also durch keine
        ''' Pruefung auf verwaiste Eintraege. Hier und nicht erst beim Anzeigen: Normalize ist der
        ''' Weg, den Laden UND Speichern nehmen, also raeumt ein einziges Speichern die Altlast
        ''' dauerhaft weg.</summary>
        Private Shared Function NormalizeResults(value As List(Of String)) As List(Of String)
            Return If(value, New List(Of String)()).
                Where(Function(p) Not String.IsNullOrWhiteSpace(p)).
                Where(Function(p) Not FileOperationPolicy.IsTrashFolder(p)).
                Distinct(StringComparer.OrdinalIgnoreCase).
                ToList()
        End Function

        Private Shared Function NormalizeConditions(value As List(Of SearchCondition)) As List(Of SearchCondition)
            Dim result As New List(Of SearchCondition)()
            For Each item In If(value, New List(Of SearchCondition)())
                If item Is Nothing Then Continue For
                If Not SearchCondition.ValidFields.Contains(item.Field, StringComparer.OrdinalIgnoreCase) Then Continue For
                If Not SearchCondition.ValidOperators.Contains(item.Operator, StringComparer.OrdinalIgnoreCase) Then Continue For
                If String.IsNullOrWhiteSpace(item.Value) Then Continue For
                result.Add(New SearchCondition With {
                    .Field = item.Field,
                    .Operator = item.Operator,
                    .Value = item.Value.Trim()
                })
            Next
            Return result
        End Function
    End Class

End Namespace
