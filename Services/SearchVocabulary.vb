Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks

Namespace Services

    ''' <summary>
    ''' Der Wortvorrat je Bild, gegen den der freie Suchtext verglichen wird. EINE Stelle fuer
    ''' jeden Suchweg: Suchfeld der Galerie im Umfang Ordner (ImageItem.SearchText plus
    ''' <see cref="CatalogWordsFor"/>), Umfang Katalog, gespeicherte Suchen und Suchdialog
    ''' (GalleryViewModel.MatchesSavedSearchText), Immich und Nextcloud nach der Vorauswahl am
    ''' Server und die mobile Seite im Browser. Was der Suchtext bedeutet (+, -, Anfuehrungszeichen,
    ''' OR), steht in <see cref="SearchTextQuery"/>.
    '''
    ''' <para>Der Vorrat: Dateiname, Stichwoerter, Katalogtexte (Titel, Beschreibung, Urheber ...),
    ''' Ort (Stadt, Land wie angezeigt und wie in der Datei), Bewertung und Favorit als Woerter,
    ''' KI-Begriffe (kanonisch und uebersetzt) und die Namen der benannten Personen auf dem Bild.
    ''' Die beiden letzten liegen in eigenen Tabellen und kommen hier je Bestand EINMAL aus dem
    ''' Katalog, nicht je Bild.</para>
    ''' </summary>
    Public NotInheritable Class SearchVocabulary

        Private Sub New()
        End Sub

        ''' <summary>Eine Momentaufnahme der Zuordnungen Pfad zu KI-Begriffen und Personennamen.
        ''' Unveraenderlich, sobald sie steht: die Leser nehmen sie einmal und lesen nur aus ihr,
        ''' ein Neuladen ersetzt sie als Ganzes. Die Listen darin aendert niemand mehr.</summary>
        Private NotInheritable Class Snapshot
            Public ReadOnly AiTags As IReadOnlyDictionary(Of String, List(Of String))
            Public ReadOnly People As IReadOnlyDictionary(Of String, List(Of String))
            ''' Der Stand des Zaehlers, als das Laden begann.
            Public ReadOnly Version As Long
            Public ReadOnly LoadedAt As Long

            Public Sub New(aiTags As Dictionary(Of String, List(Of String)), people As Dictionary(Of String, List(Of String)), version As Long)
                Me.AiTags = If(aiTags, New Dictionary(Of String, List(Of String))())
                Me.People = If(people, New Dictionary(Of String, List(Of String))())
                Me.Version = version
                LoadedAt = Environment.TickCount64
            End Sub
        End Class

        ''' <summary>Gilt eine Momentaufnahme, obwohl kein Signal kam, hoechstens so lange. Ein Netz
        ''' fuer Aenderungen am Katalog, die <see cref="Invalidate"/> nicht melden; der eigentliche
        ''' Weg ist der Zaehler.</summary>
        Private Const SafetyLifetimeMs As Long = 5 * 60 * 1000

        Private Shared _snapshot As Snapshot
        ''' <summary>Zaehlt jede gemeldete Aenderung (KI-Begriffe, Gesichter, Personen, Pfade im
        ''' Katalog). Eine Momentaufnahme mit kleinerem Stand ist veraltet.</summary>
        Private Shared _version As Long
        Private Shared ReadOnly LoadLock As New Object()
        Private Shared _backgroundLoad As Integer

        ''' <summary>Eine neue Momentaufnahme steht bereit, die im Hintergrund geladen wurde, weil
        ''' der Anzeigefaden nicht darauf warten sollte. Die Galerie siebt daraufhin neu, wenn ein
        ''' Suchtext steht. Kommt auf einem Hintergrundfaden.</summary>
        Public Shared Event Reloaded As EventHandler

        ''' <summary>Vergisst die Zuordnungen samt Speicher; der naechste Vergleich liest sie neu.
        ''' Die Galerie ruft es vor und nach einem Suchlauf ueber den Katalog.</summary>
        Public Shared Sub Forget()
            Interlocked.Increment(_version)
            Volatile.Write(_snapshot, Nothing)
        End Sub

        ''' <summary>Der Katalog hat KI-Begriffe, Gesichter, Personen oder Pfade geaendert. Die
        ''' Momentaufnahme gilt danach als veraltet; gelesen wird beim naechsten Vergleich, auf dem
        ''' Anzeigefaden im Hintergrund.</summary>
        Public Shared Sub Invalidate()
            Interlocked.Increment(_version)
        End Sub

        ''' <summary>Bewertung und Favorit als Woerter: "3 Sterne", "rating:3", drei Sterne als
        ''' Zeichen, "favorit", "is:favorite".</summary>
        Public Shared Function RatingWords(rating As Integer, isFavorite As Boolean) As String
            Dim ratingToken = $"{rating} Sterne {rating} Stern rating:{rating} bewertung:{rating} {New String("★"c, Math.Max(0, Math.Min(5, rating)))}"
            Dim favoriteToken = If(isFavorite, "favorit favorite fav is:favorite", "")
            Return ratingToken & " " & favoriteToken
        End Function

        ''' <summary>Der Ort so, wie er angezeigt wird (Landesname uebersetzt), UND der Landesname
        ''' der Datei bzw. Tabelle: wer "Germany" tippt, soll ihn genauso finden wie wer
        ''' "Deutschland" tippt.</summary>
        Public Shared Function PlaceWords(city As String, country As String, countryCode As String) As String
            Dim place = PlacePanelService.Compose(If(city, ""), PlaceLookupService.LocalizedCountry(If(countryCode, ""), If(country, "")))
            Return ImageDescriptionService.JoinForSearch(place, If(country, ""))
        End Function

        ''' <summary>Der Vorrat einer Katalogzeile ohne die Woerter aus den eigenen Tabellen
        ''' (<see cref="CatalogWordsFor"/>): Dateiname, Stichwoerter, Katalogtexte, Ort, Bewertung
        ''' und Favorit. Dieselben Teile wie ImageItem.SearchText.</summary>
        Public Shared Function ForMeta(meta As LibraryImageMeta) As String
            If meta Is Nothing Then Return ""
            Return ImageDescriptionService.JoinForSearch(
                IO.Path.GetFileName(meta.FilePath),
                String.Join(" ", If(meta.Tags, New List(Of String)())),
                meta.CatalogSearchText,
                PlaceWords(meta.City, meta.Country, meta.CountryCode),
                RatingWords(meta.Rating, meta.IsFavorite))
        End Function

        ''' <summary>Was der Katalog je Bild in eigenen Tabellen traegt: KI-Begriffe (kanonisch und in
        ''' der Anzeigeform, damit "dog" und "Hund" denselben Treffer finden) und die Namen der
        ''' benannten Personen (ohne die Sammelgruppe der Unbekannten). Leer, wenn es nichts gibt.
        ''' <paramref name="allowSingleLookups"/> aus: Serverbilder ohne eigene Abfrage je Bild
        ''' (fuer das Filtern beim Tippen auf dem Anzeigefaden).</summary>
        Public Shared Function CatalogWordsFor(filePath As String, Optional allowSingleLookups As Boolean = True) As String
            If String.IsNullOrWhiteSpace(filePath) Then Return ""
            Dim words As New List(Of String)()
            Try
                ' Serverbilder tragen einen Pseudo-Pfad und stehen in einem eigenen Index; fuer sie
                ' bleibt es beim gezielten Einzelzugriff, der den richtigen Katalog waehlt.
                Dim canonicals As List(Of String) = Nothing
                If IsServerPseudoPath(filePath) Then
                    If Not allowSingleLookups Then Return ""
                    canonicals = LibraryService.Instance.GetAiTags(filePath).Select(Function(t) t.Canonical).ToList()
                Else
                    Dim current = EnsureLoaded()
                    If current IsNot Nothing Then
                        current.AiTags.TryGetValue(filePath, canonicals)
                        Dim names As List(Of String) = Nothing
                        If current.People.TryGetValue(filePath, names) AndAlso names IsNot Nothing Then words.AddRange(names)
                    End If
                End If
                If canonicals IsNot Nothing Then
                    For Each canonical In canonicals
                        words.Add(canonical)
                        words.Add(AiTagLocalizationService.Display(canonical))
                    Next
                End If
            Catch ex As Exception
                DiagnosticLogService.LogException("SearchVocabulary.CatalogWordsFor", ex)
            End Try
            Return String.Join(" ", words.Where(Function(w) Not String.IsNullOrWhiteSpace(w)).Distinct(StringComparer.OrdinalIgnoreCase))
        End Function

        ''' <summary>Der ganze Vorrat einer Katalogzeile: <see cref="ForMeta"/> und
        ''' <see cref="CatalogWordsFor"/>.</summary>
        Public Shared Function ForMetaWithCatalogWords(meta As LibraryImageMeta) As String
            If meta Is Nothing Then Return ""
            Return ForMeta(meta) & " " & CatalogWordsFor(meta.FilePath)
        End Function

        ''' <summary>Die Momentaufnahme fuer einen Vergleich. Gilt sie (kein Signal seit dem Laden,
        ''' nicht aelter als das Netz), kommt sie sofort. Sonst laedt ein Hintergrundfaden hier und
        ''' wartet darauf. Der Anzeigefaden wartet NIE: er bekommt die bisherige Aufnahme (beim
        ''' allerersten Mal keine), das Laden laeuft im Hintergrund, und <see cref="Reloaded"/>
        ''' meldet, wenn es fertig ist.</summary>
        Private Shared Function EnsureLoaded() As Snapshot
            Dim current = Volatile.Read(_snapshot)
            If IsCurrent(current) Then Return current
            If Avalonia.Threading.Dispatcher.UIThread.CheckAccess() Then
                StartBackgroundLoad()
                Return current
            End If
            Return LoadNow()
        End Function

        Private Shared Function IsCurrent(candidate As Snapshot) As Boolean
            Return candidate IsNot Nothing AndAlso candidate.Version = Interlocked.Read(_version) AndAlso
                   Environment.TickCount64 - candidate.LoadedAt < SafetyLifetimeMs
        End Function

        ''' <summary>Laedt, wenn die Aufnahme veraltet ist; zwei Leser laden nicht doppelt (der
        ''' zweite wartet und nimmt dann die frische).</summary>
        Private Shared Function LoadNow() As Snapshot
            SyncLock LoadLock
                Dim current = Volatile.Read(_snapshot)
                If IsCurrent(current) Then Return current
                ' Der Stand VOR dem Lesen: eine Aenderung waehrend des Ladens macht die neue
                ' Aufnahme gleich wieder veraltet, statt verloren zu gehen.
                Dim version = Interlocked.Read(_version)
                Dim fresh As New Snapshot(LibraryService.Instance.GetAiTagsByPath(), LibraryService.Instance.GetPersonNamesByPath(), version)
                Volatile.Write(_snapshot, fresh)
                Return fresh
            End SyncLock
        End Function

        ''' <summary>Hoechstens so oft laedt der Hintergrund neu. Waehrend ein Durchlauf der
        ''' Gesichtserkennung Bild fuer Bild meldet, siebte die Galerie sonst nach jedem Laden
        ''' gleich wieder und loeste damit das naechste aus.</summary>
        Private Const MinBackgroundIntervalMs As Long = 2000

        Private Shared Sub StartBackgroundLoad()
            If Interlocked.CompareExchange(_backgroundLoad, 1, 0) <> 0 Then Return
            Dim previous = Volatile.Read(_snapshot)
            Dim wait = If(previous Is Nothing, 0L, MinBackgroundIntervalMs - (Environment.TickCount64 - previous.LoadedAt))
            Dim ignored = Task.Run(
                Async Function()
                    Try
                        If wait > 0 Then Await Task.Delay(TimeSpan.FromMilliseconds(wait))
                        LoadNow()
                    Catch ex As Exception
                        DiagnosticLogService.LogException("SearchVocabulary.Load", ex)
                        Return
                    Finally
                        Volatile.Write(_backgroundLoad, 0)
                    End Try
                    RaiseEvent Reloaded(Nothing, EventArgs.Empty)
                End Function)
        End Sub

        Private Shared Function IsServerPseudoPath(filePath As String) As Boolean
            Return filePath.StartsWith("immich://", StringComparison.OrdinalIgnoreCase) OrElse
                   filePath.StartsWith("nextcloud://", StringComparison.OrdinalIgnoreCase)
        End Function

    End Class

End Namespace
