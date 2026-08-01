Imports System.Collections.Generic
Imports System.Collections.ObjectModel
Imports System.Linq
Imports System.Threading.Tasks
Imports System.Windows.Input
Imports ReactiveUI
Imports FerrumPix.Models
Imports FerrumPix.Services

Namespace ViewModels

    ''' <summary>Die Personenverwaltung als eigener Bereich.
    '''
    ''' WARUM NICHT IN DEN EINSTELLUNGEN: dort stand sie zuerst, und dort war sie falsch. In den
    ''' Einstellungen legt man fest, WIE das Programm arbeitet - man arbeitet dort nicht mit seinen
    ''' Bildern. Eine Wand aus Gesichtern, die man Stueck fuer Stueck aufraeumt, ist aber genau das:
    ''' Arbeit am Bestand, wie Ordnen und Bewerten. Sie hat deshalb einen eigenen Knopf neben dem der
    ''' Einstellungen und einen eigenen Bereich.
    '''
    ''' Die zwei Schalter der Erkennung bleiben, wo Schalter hingehoeren - in den Einstellungen. Von
    ''' hier aus fuehrt ein Knopf dorthin, damit niemand suchen muss.</summary>
    Public Class PeopleViewModel
        Inherits ViewModelBase

        Private ReadOnly _mainVm As MainWindowViewModel

        Public Sub New(mainVm As MainWindowViewModel)
            _mainVm = mainVm
            RefreshPeopleCommand = ReactiveCommand.Create(Sub() RefreshPeople())
            OpenSettingsCommand = ReactiveCommand.Create(Sub() _mainVm?.OpenSettings())
        End Sub

        ''' <summary>Alle Gruppen als Kachelwand.
        '''
        ''' KACHELN UND KEINE LISTE, und das ist der ganze Punkt: eine Person erkennt man am
        ''' Gesicht, nicht an einer Zeile. Wer hundert Gruppen aufraeumen will, sucht nach
        ''' Doppelgaengern und nach Kacheln, die gar keine Person zeigen - beides sieht man in einer
        ''' Wand aus Bildern auf einen Blick und in einer Namensliste ueberhaupt nicht.</summary>
        Public ReadOnly Property People As New ObservableCollection(Of PersonListEntry)()

        ''' <summary>Wie viele Kacheln auf eine Seite kommen.
        '''
        ''' GEBLAETTERT WIRD WEGEN DER BILDER, nicht wegen der Kacheln. Jede Kachel braucht ein
        ''' Gesicht aus einer ANDEREN Datei; bei dreihundert Gruppen waeren das dreihundert Dateien,
        ''' angefasst fuer eine Wand, von der man die ersten zwanzig ansieht. Eine Seite begrenzt die
        ''' Arbeit auf das, was gerade zu sehen ist - und nebenbei die Wand auf eine Groesse, die man
        ''' noch ueberblickt.</summary>
        Public Const PeoplePageSize As Integer = 80

        Private ReadOnly _allPeople As New List(Of PersonListEntry)()
        Private ReadOnly _allFaces As New List(Of PersonFaceEntry)()
        Private ReadOnly _allFacePaths As New List(Of String)()
        Private _peoplePage As Integer
        Private _facePage As Integer

        Private _selectedPerson As PersonListEntry
        ''' <summary>ZWEI Marken, nicht eine. Die Wand und die geoeffnete Gruppe laden ihre Bilder
        ''' unabhaengig voneinander; mit einer gemeinsamen Marke wuergt der Ruecksprung aus einer
        ''' Gruppe genau das Laden der Wand ab, das gerade begonnen hat - die Kacheln blieben leer.</summary>
        Private _peopleToken As Integer
        Private _faceToken As Integer

        ''' <summary>Die geoeffnete Gruppe, oder Nothing fuer die Uebersicht. Der Bereich hat also
        ''' zwei Zustaende und keinen dritten - Wand oder eine Person, nie beides.</summary>
        Public Property SelectedPerson As PersonListEntry
            Get
                Return _selectedPerson
            End Get
            Set(value As PersonListEntry)
                Me.RaiseAndSetIfChanged(_selectedPerson, value)
                Me.RaisePropertyChanged(NameOf(IsPersonOpen))
                Me.RaisePropertyChanged(NameOf(IsPeopleOverview))
                Me.RaisePropertyChanged(NameOf(SelectedPersonName))
            End Set
        End Property

        Public ReadOnly Property IsPersonOpen As Boolean
            Get
                Return _selectedPerson IsNot Nothing
            End Get
        End Property

        Public ReadOnly Property IsPeopleOverview As Boolean
            Get
                Return _selectedPerson Is Nothing
            End Get
        End Property

        Public ReadOnly Property SelectedPersonName As String
            Get
                Return If(_selectedPerson Is Nothing, "", _selectedPerson.Name)
            End Get
        End Property

        ''' <summary>Die Gesichter der geoeffneten Gruppe. Hier raeumt man auf: was nicht dazugehoert,
        ''' fliegt einzeln heraus, ohne die uebrigen Bilder anzufassen.</summary>
        Public ReadOnly Property SelectedPersonFaces As New ObservableCollection(Of PersonFaceEntry)()

        ''' <summary>Die bereits vergebenen Namen als Vorschlaege. Siehe
        ''' <see cref="LibraryService.GetPersonNames"/>: von Hand ein zweites Mal getippt, wird aus
        ''' einer Person leicht zwei.</summary>
        Public ReadOnly Property PersonNameSuggestions As New ObservableCollection(Of String)()

        ' ── Vorschau hinter dem Auge-Zeichen ─────────────────────────────────────
        '
        ' WOZU: eine Kachel von 84 Punkten zeigt ein Gesicht, aber nicht, aus welchem Bild es
        ' stammt. Beim Aufraeumen ist genau das die Frage - gehoert dieser Kopf hierher, und ist es
        ' ueberhaupt die Aufnahme, an die ich denke. Ohne Vorschau muesste man die Verwaltung
        ' verlassen, das Bild suchen und danach von vorne anfangen.
        '
        ' Das Bild kommt aus dem Vorschau-Zwischenspeicher (ThumbnailCacheService) und NICHT ueber
        ' einen eigenen Decode: der laege bei einem RAW im Sekundenbereich, und die Kachel ist meist
        ' ohnehin schon einmal gebaut worden. Die Decode-Schleuse ist dafuer ausdruecklich nicht
        ' zustaendig (siehe DecodeGate).

        Private _previewImage As Avalonia.Media.Imaging.Bitmap
        Private _previewTitle As String = ""
        Private _previewToken As Integer

        Public Property PreviewImage As Avalonia.Media.Imaging.Bitmap
            Get
                Return _previewImage
            End Get
            Set(value As Avalonia.Media.Imaging.Bitmap)
                Me.RaiseAndSetIfChanged(_previewImage, value)
                Me.RaisePropertyChanged(NameOf(IsPreviewOpen))
            End Set
        End Property

        Public Property PreviewTitle As String
            Get
                Return _previewTitle
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_previewTitle, If(value, ""))
                Me.RaisePropertyChanged(NameOf(IsPreviewOpen))
            End Set
        End Property

        ''' <summary>Offen ist die Vorschau, sobald ein Bild ANGEFORDERT wurde - nicht erst, wenn es
        ''' da ist. Sonst blieben Klick und Erscheinen ohne Zusammenhang, und bei einer nicht
        ''' lesbaren Datei passierte gar nichts.</summary>
        Public ReadOnly Property IsPreviewOpen As Boolean
            Get
                Return Not String.IsNullOrEmpty(_previewTitle)
            End Get
        End Property

        Public Async Sub ShowPreview(entry As PersonFaceEntry)
            If entry Is Nothing OrElse String.IsNullOrWhiteSpace(entry.FilePath) Then Return
            Dim pfad = entry.FilePath
            _previewToken += 1
            Dim token = _previewToken
            PreviewImage = Nothing
            PreviewTitle = IO.Path.GetFileName(pfad)
            Try
                Dim bild = Await Task.Run(Function()
                                              Try
                                                  Dim info As New IO.FileInfo(pfad)
                                                  If Not info.Exists Then Return Nothing
                                                  Return ThumbnailCacheService.LoadOrCreate(pfad, info.LastWriteTimeUtc, info.Length)
                                              Catch ex As Exception
                                                  DiagnosticLogService.LogException("People.Preview", ex)
                                                  Return Nothing
                                              End Try
                                          End Function)
                ' Wer waehrenddessen eine andere Kachel angetippt hat, bekommt nicht dieses Bild
                ' nachgereicht - und wer die Vorschau geschlossen hat, gar keines.
                If token <> _previewToken Then Return
                PreviewImage = bild
            Catch ex As Exception
                DiagnosticLogService.LogException("People.ShowPreview", ex)
            End Try
        End Sub

        Public Sub ClosePreview()
            _previewToken += 1
            PreviewImage = Nothing
            PreviewTitle = ""
        End Sub

        Public ReadOnly Property HasPeopleFeature As Boolean
            Get
                Return FaceDetectionService.Enabled
            End Get
        End Property

        Public ReadOnly Property PeopleSummaryText As String
            Get
                ' Ueber die GESAMTLISTE, nicht ueber die angezeigte Seite: die Zahl beantwortet die
                ' Frage "wie viel habe ich", und die haengt nicht daran, wo man gerade blaettert.
                If _allPeople.Count = 0 Then Return LocalizationService.T("Noch keine Personen erkannt")
                ' NICHT _allPeople.Count(...): VB liest das als Indexzugriff auf die Eigenschaft
                ' Count und findet die LINQ-Fassung gar nicht erst.
                Dim benannt = _allPeople.Where(Function(p) p.IsNamed).Count()
                Return $"{_allPeople.Count} " & LocalizationService.T("Gruppen") & ", " &
                       $"{benannt} " & LocalizationService.T("davon benannt")
            End Get
        End Property

        ' ── Blaettern ────────────────────────────────────────────────────────────

        ''' <summary>Die gezeigte Seite, von aussen lesbar: die Ansicht blaettert selbst, weil sie
        ''' danach noch rollen muss - und das kann kein ViewModel.</summary>
        Public ReadOnly Property PeoplePage As Integer
            Get
                Return _peoplePage
            End Get
        End Property

        Public ReadOnly Property FacePage As Integer
            Get
                Return _facePage
            End Get
        End Property

        Public ReadOnly Property PeoplePageText As String
            Get
                Return SeitenText(_peoplePage, _allPeople.Count)
            End Get
        End Property

        Public ReadOnly Property FacePageText As String
            Get
                Return SeitenText(_facePage, _allFaces.Count)
            End Get
        End Property

        ''' <summary>"Seite 2 von 5, 61 bis 120 von 263". Die zweite Haelfte steht dabei, weil sie
        ''' die eigentliche Frage beantwortet: wie viel ist noch da.</summary>
        Private Shared Function SeitenText(seite As Integer, gesamt As Integer) As String
            If gesamt = 0 Then Return ""
            Dim seiten = Math.Max(1, CInt(Math.Ceiling(gesamt / CDbl(PeoplePageSize))))
            Dim von = seite * PeoplePageSize + 1
            Dim bis = Math.Min(gesamt, (seite + 1) * PeoplePageSize)
            Return $"{LocalizationService.T("Seite")} {seite + 1} {LocalizationService.T("von")} {seiten}" &
                   $"  ·  {von} {LocalizationService.T("bis")} {bis} {LocalizationService.T("von")} {gesamt}"
        End Function

        Public ReadOnly Property HasPeoplePaging As Boolean
            Get
                Return _allPeople.Count > PeoplePageSize
            End Get
        End Property

        Public ReadOnly Property HasFacePaging As Boolean
            Get
                Return _allFaces.Count > PeoplePageSize
            End Get
        End Property

        Public ReadOnly Property CanPeoplePageBack As Boolean
            Get
                Return _peoplePage > 0
            End Get
        End Property

        Public ReadOnly Property CanPeoplePageForward As Boolean
            Get
                Return (_peoplePage + 1) * PeoplePageSize < _allPeople.Count
            End Get
        End Property

        Public ReadOnly Property CanFacePageBack As Boolean
            Get
                Return _facePage > 0
            End Get
        End Property

        Public ReadOnly Property CanFacePageForward As Boolean
            Get
                Return (_facePage + 1) * PeoplePageSize < _allFaces.Count
            End Get
        End Property

        ' Nur diese beiden als Befehl: Oeffnen, Zurueck und Blaettern muessen danach ROLLEN, und das
        ' kann nur die Ansicht - sie rufen die Methoden deshalb direkt (PeopleView.axaml.vb).
        Public ReadOnly Property RefreshPeopleCommand As ICommand
        Public ReadOnly Property OpenSettingsCommand As ICommand

        ''' <summary>Baut die Wand neu auf.
        '''
        ''' Die Bilder holt der Dienst im Hintergrund nach, verkleinert dekodiert und durch die
        ''' Decode-Schleuse - bei hundert Gruppen waere ein voller Decode je Kachel im Vordergrund
        ''' eine Vollbremsung.
        '''
        ''' Die Marke haelt einen spaeten Nachschub von der naechsten Wand fern: wer waehrend des
        ''' Ladens aktualisiert, bekaeme sonst Gesichter, die zu Kacheln von vorhin gehoeren.</summary>
        ''' <summary>Die schon geholten Aushaengeschilder, ueber Person UND Bildstelle verschluesselt.
        '''
        ''' WOZU: jeder Aufbau der Wand legt neue Eintraege an, und mit ihnen waren die Bilder weg -
        ''' zurueck aus einer Gruppe, ein Klick auf "Aktualisieren", ein erneutes Oeffnen des
        ''' Bereichs, und dieselben Dateien wurden wieder aufgemacht. Bei hundert Gruppen sind das
        ''' hundert Dateien fuer eine Wand, die genauso schon dastand.
        '''
        ''' Der Schluessel traegt die BILDSTELLE mit: wird ein Gesicht herausgeloest, uebernimmt ein
        ''' anderes das Aushaengeschild - dann steht ein neuer Schluessel da und wird geholt, statt
        ''' den alten Kopf weiterzuzeigen.
        '''
        ''' Grenzenlos waechst er nicht: beim Aufbau fliegt heraus, was in der neuen Liste nicht mehr
        ''' vorkommt. Damit haengt er an der Zahl der Gruppen und nicht an der Zahl der Durchgaenge.</summary>
        Private ReadOnly _coverCache As New Dictionary(Of String, Avalonia.Media.Imaging.Bitmap)(StringComparer.Ordinal)

        Private Shared Function CoverKey(personId As String, coverPath As String, x As Double, y As Double) As String
            Return $"{personId}|{coverPath}|{x:0}|{y:0}"
        End Function

        Public Sub RefreshPeople()
            SelectedPerson = Nothing
            SelectedPersonFaces.Clear()
            ' Was schon geholt wurde, VOR dem Wegwerfen der Eintraege einsammeln.
            For Each alt In _allPeople
                If alt.Cover Is Nothing OrElse String.IsNullOrEmpty(alt.CoverPath) Then Continue For
                _coverCache(CoverKey(alt.Id, alt.CoverPath, alt.BoxX, alt.BoxY)) = alt.Cover
            Next
            _allPeople.Clear()
            Try
                If FaceDetectionService.Enabled Then
                    For Each person In LibraryService.Instance.GetPeople()
                        ' Gruppen OHNE Bild gehoeren nicht in eine Wand aus Gesichtern: eine Kachel
                        ' ohne Bild und mit "0 Bilder" ist nichts, woran man arbeiten koennte. Sie
                        ' entstehen im Betrieb - beim Verschmelzen, beim Verschieben, bei einem
                        ' erneuten Durchlauf. Die namenlosen darunter raeumt "Datenbank aufraeumen"
                        ' weg, eine benannte bleibt bestehen: ihr Name ist Handarbeit.
                        If person.ImageCount <= 0 Then Continue For
                        Dim entry As New PersonListEntry(person.Id, person.Name, person.ImageCount, person.IsUnknownBin)
                        ' Die LAGE des Aushaengeschilds kommt gleich mit; das Bild dazu erst, wenn
                        ' die Kachel auf einer angezeigten Seite steht.
                        Dim cover = LibraryService.Instance.GetPersonCover(person.Id)
                        entry.CoverPath = cover.FilePath
                        entry.BoxX = cover.X
                        entry.BoxY = cover.Y
                        entry.BoxWidth = cover.Width
                        entry.BoxHeight = cover.Height
                        ' Schon einmal geholt? Dann steht die Kachel sofort - ohne Datei, ohne
                        ' Decode und ohne das Nachrutschen von oben nach unten.
                        Dim gemerkt As Avalonia.Media.Imaging.Bitmap = Nothing
                        If _coverCache.TryGetValue(CoverKey(entry.Id, entry.CoverPath, entry.BoxX, entry.BoxY), gemerkt) Then
                            entry.Cover = gemerkt
                        End If
                        _allPeople.Add(entry)
                    Next
                End If
            Catch ex As Exception
                DiagnosticLogService.LogException("People.RefreshPeople", ex)
            End Try

            ' Ausmisten: was in der neuen Liste nicht mehr vorkommt, wird auch nicht mehr gebraucht -
            ' geloeschte Gruppen, verschmolzene, gewechselte Aushaengeschilder. Ohne das waere der
            ' Zwischenspeicher an der Zahl der DURCHGAENGE gewachsen statt an der der Gruppen.
            Dim lebend As New HashSet(Of String)(StringComparer.Ordinal)
            For Each entry In _allPeople
                If Not String.IsNullOrEmpty(entry.CoverPath) Then
                    lebend.Add(CoverKey(entry.Id, entry.CoverPath, entry.BoxX, entry.BoxY))
                End If
            Next
            For Each tot In _coverCache.Keys.Where(Function(k) Not lebend.Contains(k)).ToList()
                _coverCache.Remove(tot)
            Next

            RefreshNameSuggestions()
            Me.RaisePropertyChanged(NameOf(PeopleSummaryText))
            Me.RaisePropertyChanged(NameOf(HasPeopleFeature))
            ShowPeoplePage(0)
        End Sub

        ''' <summary>Die Vorschlagsliste neu holen. Nach jedem Benennen: der neue Name soll beim
        ''' naechsten Feld schon zur Wahl stehen, sonst tippt man ihn wieder von Hand.</summary>
        Public Sub RefreshNameSuggestions()
            PersonNameSuggestions.Clear()
            Try
                For Each name In LibraryService.Instance.GetPersonNames()
                    PersonNameSuggestions.Add(name)
                Next
            Catch ex As Exception
                DiagnosticLogService.LogException("People.RefreshNameSuggestions", ex)
            End Try
        End Sub

        ''' <summary>Zeigt eine Seite der Kachelwand und holt NUR fuer sie die Gesichter.
        '''
        ''' Die Marke wird bei jedem Seitenwechsel neu vergeben: wer weiterblaettert, waehrend die
        ''' vorige Seite noch laedt, bekaeme sonst deren Bilder nachgereicht - in Kacheln, die
        ''' inzwischen jemand anderen zeigen.</summary>
        Public Sub ShowPeoplePage(seite As Integer)
            Dim letzte = Math.Max(0, CInt(Math.Ceiling(_allPeople.Count / CDbl(PeoplePageSize))) - 1)
            _peoplePage = Math.Max(0, Math.Min(seite, letzte))
            _peopleToken += 1
            Dim token = _peopleToken

            People.Clear()
            Dim aufSeite = _allPeople.Skip(_peoplePage * PeoplePageSize).Take(PeoplePageSize).ToList()
            For Each entry In aufSeite
                People.Add(entry)
            Next
            For Each name In {NameOf(PeoplePageText), NameOf(HasPeoplePaging),
                              NameOf(CanPeoplePageBack), NameOf(CanPeoplePageForward)}
                Me.RaisePropertyChanged(name)
            Next
            FacePanelService.LoadCovers(aufSeite, Function() token = _peopleToken)
        End Sub

        ''' <summary>Oeffnet eine Gruppe: alle ihre Gesichter, das groesste zuerst.</summary>
        Public Sub OpenPerson(person As PersonListEntry)
            If person Is Nothing Then Return
            SelectedPersonFaces.Clear()
            _allFaces.Clear()
            _allFacePaths.Clear()
            SelectedPerson = person
            Try
                For Each face In LibraryService.Instance.GetFacesForPerson(person.Id)
                    Dim entry As New PersonFaceEntry(face.FaceId, person.Id, person.Name)
                    entry.SetBox(face.X, face.Y, face.Width, face.Height)
                    ' Der Pfad haengt am Eintrag UND steht in der Parallelliste: die Liste braucht
                    ' der Nachlader (eine Datei je Kachel, der Reihe nach), der Eintrag die Vorschau
                    ' hinter dem Auge-Zeichen - die wird an einer einzelnen Kachel angefordert.
                    entry.FilePath = face.FilePath
                    _allFaces.Add(entry)
                    _allFacePaths.Add(face.FilePath)
                Next
            Catch ex As Exception
                DiagnosticLogService.LogException("People.OpenPerson", ex)
            End Try
            ShowFacePage(0)
        End Sub

        ''' <summary>Eine Seite der Gesichter einer Gruppe. Auch hier gilt: eine Person mit
        ''' dreihundert Bildern hiesse sonst dreihundert Dateien anfassen, um sechzig anzusehen.</summary>
        Public Sub ShowFacePage(seite As Integer)
            Dim letzte = Math.Max(0, CInt(Math.Ceiling(_allFaces.Count / CDbl(PeoplePageSize))) - 1)
            _facePage = Math.Max(0, Math.Min(seite, letzte))
            _faceToken += 1
            Dim token = _faceToken

            SelectedPersonFaces.Clear()
            Dim erstes = _facePage * PeoplePageSize
            Dim anzahl = Math.Min(PeoplePageSize, Math.Max(0, _allFaces.Count - erstes))
            Dim aufSeite = _allFaces.Skip(erstes).Take(anzahl).ToList()
            Dim pfade = _allFacePaths.Skip(erstes).Take(anzahl).ToList()
            For Each entry In aufSeite
                SelectedPersonFaces.Add(entry)
            Next
            For Each name In {NameOf(FacePageText), NameOf(HasFacePaging),
                              NameOf(CanFacePageBack), NameOf(CanFacePageForward)}
                Me.RaisePropertyChanged(name)
            Next
            FacePanelService.LoadFacesFromManyFiles(aufSeite, pfade, Function() token = _faceToken)
        End Sub

        Public Sub ClosePerson()
            ' NUR die Gesichtermarke: die Wand laedt unabhaengig davon, und ihr Nachschub darf
            ' beim Zurueckgehen nicht abbrechen.
            _faceToken += 1
            SelectedPersonFaces.Clear()
            _allFaces.Clear()
            _allFacePaths.Clear()
            SelectedPerson = Nothing
        End Sub

        ''' <summary>Benennt die geoeffnete Gruppe. Ueber dieselbe Bibliotheksregel wie im Infopanel:
        ''' ein vorhandener Name verschmilzt beide Gruppen.</summary>
        Public Sub RenameSelectedPerson(newName As String)
            If _selectedPerson Is Nothing Then Return
            Dim id = _selectedPerson.Id
            Try
                LibraryService.Instance.NamePerson(id, newName)
            Catch ex As Exception
                DiagnosticLogService.LogException("People.RenamePerson", ex)
                Return
            End Try

            ' Hiess schon jemand so, hat die Bibliothek die beiden Gruppen VERSCHMOLZEN - diese hier
            ' gibt es dann nicht mehr, und die Wand muss neu gebaut werden. Sonst bleibt die Gruppe
            ' offen und bekommt nur ihren neuen Namen: wer gerade tippt, will weiterarbeiten und
            ' nicht zurueckgeworfen werden.
            Dim nochDa = LibraryService.Instance.GetPeople().Any(Function(p) String.Equals(p.Id, id, StringComparison.Ordinal))
            If Not nochDa Then
                RefreshPeople()
                Return
            End If

            _selectedPerson.Name = If(newName, "").Trim()
            RefreshNameSuggestions()
            Me.RaisePropertyChanged(NameOf(SelectedPersonName))
            Me.RaisePropertyChanged(NameOf(PeopleSummaryText))
        End Sub

        ''' <summary>Loest EIN Gesicht aus der geoeffneten Gruppe. Die Kachel verschwindet sofort -
        ''' sie gehoert ja nicht mehr dazu.</summary>
        Public Sub DetachFaceFromSelectedPerson(faceId As String)
            If String.IsNullOrWhiteSpace(faceId) Then Return
            Try
                LibraryService.Instance.DetachFace(faceId)
            Catch ex As Exception
                DiagnosticLogService.LogException("People.DetachFace", ex)
                Return
            End Try
            ' Aus BEIDEN Listen: die Seite zeigt nur einen Ausschnitt, die Gesamtliste traegt den
            ' Rest - bliebe es dort stehen, waere es beim naechsten Blaettern wieder da.
            Dim weg = _allFaces.FirstOrDefault(Function(f) String.Equals(f.FaceId, faceId, StringComparison.Ordinal))
            If weg IsNot Nothing Then
                Dim stelle = _allFaces.IndexOf(weg)
                _allFaces.RemoveAt(stelle)
                If stelle < _allFacePaths.Count Then _allFacePaths.RemoveAt(stelle)
                SelectedPersonFaces.Remove(weg)
            End If

            ' LEER HEISST WEG. Wer das letzte Gesicht herausloest, hat gesagt, dass diese Gruppe
            ' keine Person ist - dann braucht es keinen zweiten Handgriff dafuer. Die Gruppe
            ' verschwindet, und die Ansicht kehrt zur Wand zurueck: eine geoeffnete Gruppe ohne
            ' Inhalt waere eine Sackgasse.
            If _allFaces.Count = 0 Then
                DeleteSelectedPerson()
                Return
            End If

            ' Die Kachel muss nachziehen: war das herausgeloeste Gesicht ihr Aushaengeschild, zeigte
            ' sie sonst weiter genau den Kopf, den man gerade als falsch aussortiert hat. Und die
            ' Zahl daneben stimmt ohnehin nicht mehr.
            RefreshSelectedPersonCover()

            For Each name In {NameOf(FacePageText), NameOf(HasFacePaging),
                              NameOf(CanFacePageBack), NameOf(CanFacePageForward)}
                Me.RaisePropertyChanged(name)
            Next
        End Sub

        ''' <summary>Holt Aushaengeschild und Anzahl der geoeffneten Gruppe neu.
        '''
        ''' Das Bild wird nur dann neu geladen, wenn es wirklich ein anderes ist - sonst flackerte
        ''' die Kachel bei jedem Handgriff.</summary>
        Private Sub RefreshSelectedPersonCover()
            If _selectedPerson Is Nothing Then Return
            Try
                _selectedPerson.ImageCount = LibraryService.Instance.GetPersonImageCount(_selectedPerson.Id)

                Dim cover = LibraryService.Instance.GetPersonCover(_selectedPerson.Id)
                Dim gleich = String.Equals(cover.FilePath, _selectedPerson.CoverPath, StringComparison.Ordinal) AndAlso
                             Math.Abs(cover.X - _selectedPerson.BoxX) < 0.5 AndAlso
                             Math.Abs(cover.Y - _selectedPerson.BoxY) < 0.5
                If gleich Then Return

                _selectedPerson.CoverPath = cover.FilePath
                _selectedPerson.BoxX = cover.X
                _selectedPerson.BoxY = cover.Y
                _selectedPerson.BoxWidth = cover.Width
                _selectedPerson.BoxHeight = cover.Height
                _selectedPerson.Cover = Nothing

                ' KEINE neue Marke: die wuerde das Nachladen der uebrigen Gesichter dieser Gruppe
                ' abwuergen, das gerade laeuft. Gefragt wird stattdessen, ob dieselbe Gruppe noch
                ' offen ist - genauer geht es hier nicht, und es stoert nichts.
                Dim offen = _selectedPerson
                FacePanelService.LoadCovers({offen}, Function() _selectedPerson Is offen)
            Catch ex As Exception
                DiagnosticLogService.LogException("People.RefreshSelectedPersonCover", ex)
            End Try
        End Sub

        ''' <summary>Wirft die geoeffnete Gruppe weg und kehrt zur Wand zurueck.
        '''
        ''' KEIN eigener Knopf: gerufen wird das, wenn das LETZTE Gesicht herausgeloest wurde. Wer
        ''' alle Gesichter herausnimmt, hat damit schon gesagt, dass die Gruppe keine Person ist -
        ''' ein zweiter Handgriff dafuer waere eine Frage, die der Benutzer gerade beantwortet hat.
        ''' Sind noch Gesichter da, bleiben sie als Funde bestehen und gehoeren zu niemandem.</summary>
        Public Sub DeleteSelectedPerson()
            If _selectedPerson Is Nothing Then Return
            Try
                LibraryService.Instance.DeletePerson(_selectedPerson.Id)
            Catch ex As Exception
                DiagnosticLogService.LogException("People.DeletePerson", ex)
            End Try
            RefreshPeople()
        End Sub

    End Class

End Namespace
