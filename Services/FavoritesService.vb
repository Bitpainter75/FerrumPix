Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Text.Json

Namespace Services

    ''' <summary>
    ''' Ein Favorit in der Galerie-Seitenleiste. Alles Navigierbare kann Favorit werden: ein
    ''' Ordner, ein Immich-Knoten (Alle Fotos, Album, Person, Ort) oder eine gespeicherte Suche.
    ''' Der Eintrag traegt nur die IDENTITAET des Ziels - der Favoriten-Tab baut daraus beim
    ''' Anzeigen wieder einen echten Navigationsknoten, damit ein Klick genau dasselbe tut wie im
    ''' Herkunfts-Tab (und eine inzwischen geloeschte Suche/ein fehlender Ordner auffaellt).
    ''' </summary>
    Public Class FavoriteEntry
        ''' "Folder", "Immich" oder "Search".
        Public Property Kind As String = "Folder"
        ''' Anzeigename in der Liste (frei umbenennbar, Standard = Name des Ziels).
        Public Property Name As String = ""
        ''' Nur bei Kind="Folder": absoluter Ordnerpfad.
        Public Property Path As String = ""
        ''' Nur bei Kind="Search": Id des SearchListEntry.
        Public Property SearchId As String = ""
        ''' Nur bei Kind="Immich": Knotenart (ImmichAll/ImmichAlbum/ImmichPerson/ImmichPlace) und
        ''' die Ziel-Id des Servers (Album-/Personen-Id bzw. Ortsname). Beides zusammen macht den
        ''' Knoten eindeutig - eine Album-Id kann dieselbe Zeichenfolge sein wie ein Ortsname.
        Public Property ImmichKind As String = ""
        Public Property ImmichId As String = ""

        ''' <summary>Stabiler Schluessel eines Favoriten - damit derselbe Ordner/Knoten nicht
        ''' doppelt in der Liste landet und "Zu Favoriten hinzufuegen" beim zweiten Mal nichts tut
        ''' verhaelt.</summary>
        Public ReadOnly Property Key As String
            Get
                Select Case NormalizeKind(Kind)
                    Case "Search" : Return "search|" & If(SearchId, "").ToLowerInvariant()
                    Case "Immich" : Return "immich|" & If(ImmichKind, "").ToLowerInvariant() & "|" & If(ImmichId, "").ToLowerInvariant()
                    Case Else : Return "folder|" & NormalizePath(Path)
                End Select
            End Get
        End Property

        ''' <summary>Ordnerpfad für den Schluessel normalisieren - die Gross-/Kleinschreibung wird nur
        ''' unter Windows eingeebnet. Auf Linux und macOS-Dateisystemen mit Beachtung der
        ''' Gross-/Kleinschreibung sind /Bilder/RAW und /Bilder/raw ZWEI Ordner; ein gemeinsames
        ''' ToLowerInvariant hatte sie zu einem Favoriten verschmolzen, sodass der zweite sich nicht
        ''' anlegen liess und das Entfernen den falschen Eintrag treffen konnte.
        ''' Beide Trennzeichen werden abgeschnitten: unter Windows ist AltDirectorySeparatorChar der
        ''' Schraegstrich, und ein von Hand eingetragener Pfad kann auf jedem von beiden enden.</summary>
        Friend Shared Function NormalizePath(value As String) As String
            Dim p = If(value, "").TrimEnd(IO.Path.DirectorySeparatorChar, IO.Path.AltDirectorySeparatorChar)
            Return If(OperatingSystem.IsWindows(), p.ToLowerInvariant(), p)
        End Function

        Friend Shared Function NormalizeKind(value As String) As String
            Select Case If(value, "").Trim().ToLowerInvariant()
                Case "search" : Return "Search"
                Case "immich" : Return "Immich"
                Case Else : Return "Folder"
            End Select
        End Function
    End Class

    ''' <summary>Speichert die Favoriten der Galerie-Seitenleiste - eigene Datei neben den
    ''' Suchlisten (gleiches Muster wie SearchListService), damit sie unabhaengig von den
    ''' Anwendungseinstellungen gesichert/uebertragen werden koennen.</summary>
    Public NotInheritable Class FavoritesService

        Private Sub New()
        End Sub

        Private Shared ReadOnly FavoritesDirectory As String =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FerrumPix")

        Private Shared ReadOnly FavoritesPath As String =
            Path.Combine(FavoritesDirectory, "favorites.json")

        Private Shared ReadOnly JsonOptions As New JsonSerializerOptions With {.WriteIndented = True}

        Public Shared Function Load() As List(Of FavoriteEntry)
            Try
                If Not File.Exists(FavoritesPath) Then Return New List(Of FavoriteEntry)()
                Return Normalize(JsonSerializer.Deserialize(Of List(Of FavoriteEntry))(File.ReadAllText(FavoritesPath)))
            Catch
                Return New List(Of FavoriteEntry)()
            End Try
        End Function

        Public Shared Sub Save(entries As IEnumerable(Of FavoriteEntry))
            Try
                Directory.CreateDirectory(FavoritesDirectory)
                File.WriteAllText(FavoritesPath, JsonSerializer.Serialize(Normalize(entries?.ToList()), JsonOptions))
            Catch
            End Try
        End Sub

        ''' <summary>Legt den Favoriten an, wenn er noch nicht da ist. Liefert True, wenn sich die
        ''' Liste geaendert hat (der Aufrufer meldet es dann in der Statuszeile).</summary>
        Public Shared Function Add(entry As FavoriteEntry) As Boolean
            If entry Is Nothing Then Return False
            Dim entries = Load()
            If entries.Any(Function(f) String.Equals(f.Key, entry.Key, StringComparison.Ordinal)) Then Return False
            entries.Add(entry)
            Save(entries)
            Return True
        End Function

        Public Shared Function Remove(key As String) As Boolean
            If String.IsNullOrWhiteSpace(key) Then Return False
            Dim entries = Load()
            Dim removed = entries.RemoveAll(Function(f) String.Equals(f.Key, key, StringComparison.Ordinal))
            If removed <= 0 Then Return False
            Save(entries)
            Return True
        End Function

        Public Shared Function Contains(key As String) As Boolean
            If String.IsNullOrWhiteSpace(key) Then Return False
            Return Load().Any(Function(f) String.Equals(f.Key, key, StringComparison.Ordinal))
        End Function

        Public Shared Function Normalize(value As List(Of FavoriteEntry)) As List(Of FavoriteEntry)
            Dim result As New List(Of FavoriteEntry)()
            Dim seen As New HashSet(Of String)(StringComparer.Ordinal)
            For Each item In If(value, New List(Of FavoriteEntry)())
                If item Is Nothing Then Continue For
                item.Kind = FavoriteEntry.NormalizeKind(item.Kind)
                item.Name = If(item.Name, "").Trim()
                item.Path = If(item.Path, "").Trim()
                item.SearchId = If(item.SearchId, "").Trim()
                item.ImmichKind = If(item.ImmichKind, "").Trim()
                item.ImmichId = If(item.ImmichId, "").Trim()

                ' Ein Favorit ohne Ziel waere eine tote Zeile - er faellt beim Laden still weg.
                Select Case item.Kind
                    Case "Folder" : If String.IsNullOrWhiteSpace(item.Path) Then Continue For
                    Case "Search" : If String.IsNullOrWhiteSpace(item.SearchId) Then Continue For
                    Case "Immich" : If String.IsNullOrWhiteSpace(item.ImmichKind) Then Continue For
                End Select
                If String.IsNullOrWhiteSpace(item.Name) Then
                    item.Name = If(item.Kind = "Folder", Path.GetFileName(item.Path.TrimEnd(IO.Path.DirectorySeparatorChar)), item.Kind)
                End If
                If seen.Add(item.Key) Then result.Add(item)
            Next
            Return result
        End Function

    End Class

End Namespace
