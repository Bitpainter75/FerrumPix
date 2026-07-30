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

        Public Shared ReadOnly ValidFields As String() = {"Width", "Height", "Camera", "Iso", "Aperture", "FocalLength", "DateTaken"}
        Public Shared ReadOnly ValidOperators As String() = {">", "<", ">=", "<=", "=", "Contains"}
    End Class

    Public Class SearchListEntry
        Public Property Id As String = Guid.NewGuid().ToString("N")
        Public Property Name As String = ""
        ''' "Local" (Dateisystem-Scan) oder "Immich" (Server-Suche). Immich nur nutzbar, wenn konfiguriert.
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
                    .Source = If(String.Equals(item.Source, "Immich", StringComparison.OrdinalIgnoreCase), "Immich", "Local"),
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
                    .Results = If(item.Results, New List(Of String)()).
                        Where(Function(p) Not String.IsNullOrWhiteSpace(p)).
                        Distinct(StringComparer.OrdinalIgnoreCase).
                        ToList(),
                    .Conditions = NormalizeConditions(item.Conditions),
                    .ConditionCombinator = If(String.Equals(item.ConditionCombinator, "OR", StringComparison.OrdinalIgnoreCase), "OR", "AND")
                })
            Next
            Return result
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
