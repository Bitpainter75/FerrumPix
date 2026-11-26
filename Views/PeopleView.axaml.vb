Imports Avalonia.Controls
Imports Avalonia.Input
Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml
Imports FerrumPix.Models
Imports FerrumPix.ViewModels

Namespace Views

    ''' <summary>Die Personenverwaltung. Frueher ein Abschnitt der Einstellungen, jetzt ein eigener
    ''' Bereich mit eigenem Knopf in der Galerie - siehe <see cref="PeopleViewModel"/>.</summary>
    Public Class PeopleView
        Inherits UserControl
        Implements IModeView

        ''' <summary>Inhalte aus einem DataTemplate entstehen oft erst NACH dem Sprachdurchlauf
        ''' ueber das Fenster. Jedes neu materialisierte Element uebersetzt deshalb seinen eigenen
        ''' Teilbaum - ueber BEIDE Baeume, siehe
        ''' <see cref="LocalizationService.ApplyToMaterialized"/>.</summary>
        Private Sub OnLocalizedItemAttachedToVisualTree(sender As Object, e As Avalonia.VisualTreeAttachmentEventArgs)
            Dim itemRoot = TryCast(sender, Avalonia.Visual)
            If itemRoot IsNot Nothing Then FerrumPix.Services.LocalizationService.ApplyToMaterialized(itemRoot)
        End Sub

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
            AddHandler Me.AttachedToVisualTree, AddressOf OnAttachedFocus
        End Sub

        ''' <summary>Beim Erscheinen den Fokus holen, sonst ist Escape tot.
        '''
        ''' Ein KeyDown an einer Ansicht laeuft nur, wenn der Fokus in ihrem Teilbaum sitzt. Der
        ''' Bereich wird aber ueber einen Knopf in der Galerie geoeffnet - der Fokus bleibt dort, und
        ''' Escape kam hier nie an. Es half nur, vorher irgendwohin zu klicken, und genau so
        ''' willkuerlich fuehlte es sich an (dieselbe Falle wie nach einem Overlay-Dialog, siehe
        ''' MainWindow.RestoreFocusAfterDialog).
        '''
        ''' Ueber den Dispatcher und nicht sofort: beim Anhaengen steht das Layout noch nicht, und
        ''' ein Fokus auf ein Element ohne Flaeche verpufft.</summary>
        Private Sub OnAttachedFocus(sender As Object, e As EventArgs)
            Avalonia.Threading.Dispatcher.UIThread.Post(Sub() Me.Focus())
        End Sub

        ''' <summary>Aus demselben Grund beim erneuten Betreten: seit die Ansichten behalten werden,
        ''' haengt diese nur beim ersten Mal an den Baum, und Escape waere danach wieder tot.</summary>
        Public Sub OnModeEntered() Implements IModeView.OnModeEntered
            Avalonia.Threading.Dispatcher.UIThread.Post(Sub() Me.Focus())
        End Sub

        ''' <summary>Zurueck an den Anfang. Nach jedem Wechsel - Gruppe auf, Gruppe zu, Seite
        ''' weiter: die neue Ansicht faengt oben an und nicht dort, wo die vorige aufgehoert hat.
        ''' Sonst sieht man nach einem Klick mitten in eine fremde Kachelreihe.</summary>
        Private Sub ScrollToTop()
            Dim sv = Me.FindControl(Of ScrollViewer)("PeopleScrollViewer")
            If sv Is Nothing Then Return
            sv.Offset = New Avalonia.Vector(0, 0)
        End Sub

        Private Sub OnBackClick(sender As Object, e As RoutedEventArgs)
            TryCast(TopLevel.GetTopLevel(Me)?.DataContext, MainWindowViewModel)?.CloseSecondaryMode()
            e.Handled = True
        End Sub

        ''' <summary>Das Auge auf der Kachel: das ganze Bild ansehen, ohne den Bereich zu verlassen.
        ''' Ein Ausschnitt von 84 Punkten sagt, WER zu sehen ist, aber nicht, aus welchem Bild.</summary>
        Private Sub OnPreviewFaceClick(sender As Object, e As RoutedEventArgs)
            Dim entry = TryCast(TryCast(sender, Button)?.DataContext, PersonFaceEntry)
            If entry Is Nothing Then Return
            TryCast(Me.DataContext, PeopleViewModel)?.ShowPreview(entry)
            e.Handled = True
        End Sub

        ''' <summary>Ein Klick irgendwo schliesst die Vorschau. PointerPressed und nicht Click: der
        ''' Grund ist kein Knopf, sondern eine Flaeche - ein Knopf, der den halben Bereich einnimmt,
        ''' waere in der Bedienung dasselbe und in der Tastaturreihenfolge ein Fremdkoerper.</summary>
        Private Sub OnClosePreviewClick(sender As Object, e As PointerPressedEventArgs)
            TryCast(Me.DataContext, PeopleViewModel)?.ClosePreview()
            e.Handled = True
        End Sub

        ''' <summary>Ein Klick in die Zeile mit dem Namensfeld gehoert dem Feld und schliesst die
        ''' Vorschau nicht.</summary>
        Private Sub OnPreviewInputPointerPressed(sender As Object, e As PointerPressedEventArgs)
            e.Handled = True
        End Sub

        ''' <summary>Die Eingabetaste ordnet zu. Steht die Vorschlagsliste offen, hat der Rahmen die
        ''' Taste schon fuer die Auswahl verbraucht: der erste Druck waehlt den Vorschlag, der zweite
        ''' ordnet zu.</summary>
        Private Sub OnPreviewNameKeyDown(sender As Object, e As KeyEventArgs)
            If e.Key <> Key.Enter Then Return
            AssignPreviewFace()
            e.Handled = True
        End Sub

        Private Sub OnAssignPreviewFaceClick(sender As Object, e As RoutedEventArgs)
            AssignPreviewFace()
            e.Handled = True
        End Sub

        Private Sub AssignPreviewFace()
            Dim box = Me.FindControl(Of AutoCompleteBox)("PreviewNameBox")
            Dim vm = TryCast(Me.DataContext, PeopleViewModel)
            If box Is Nothing OrElse vm Is Nothing Then Return
            vm.AssignPreviewFace(If(box.Text, ""))
        End Sub

        Public Shadows Sub OnKeyDown(sender As Object, e As KeyEventArgs)
            If e.Key <> Key.Escape Then Return
            Dim vmVorschau = TryCast(DataContext, PeopleViewModel)
            ' Zuerst die Vorschau: Escape geht immer nur EINE Ebene zurueck.
            If vmVorschau IsNot Nothing AndAlso vmVorschau.IsPreviewOpen Then
                vmVorschau.ClosePreview()
                e.Handled = True
                Return
            End If
            ' Dann eine geoeffnete Gruppe, in der Wand eine Markierung, erst danach der Bereich selbst.
            Dim vm = TryCast(DataContext, PeopleViewModel)
            If vm IsNot Nothing AndAlso vm.IsPeopleOverview AndAlso vm.HasGroupSelection Then
                vm.ClearGroupSelection()
            ElseIf vm IsNot Nothing AndAlso vm.IsPersonOpen Then
                vm.ClosePerson()
                ScrollToTop()
            Else
                TryCast(TopLevel.GetTopLevel(Me)?.DataContext, MainWindowViewModel)?.CloseSecondaryMode()
            End If
            e.Handled = True
        End Sub

        ''' <summary>Eine Gruppe oeffnen. Ueber den Datenkontext der Kachel und nicht ueber einen
        ''' Befehl, weil danach noch etwas geschehen muss, das nur die Ansicht kann: rollen.</summary>
        Private Sub OnPersonCardClick(sender As Object, e As RoutedEventArgs)
            Dim entry = TryCast(TryCast(sender, Button)?.DataContext, PersonListEntry)
            If entry Is Nothing Then Return
            Dim vm = TryCast(Me.DataContext, PeopleViewModel)
            If vm Is Nothing Then Return
            ' Steht eine Markierung, markiert auch der Klick auf die Kachel: wer zwanzig Gruppen
            ' zusammenstellt, soll nicht zwanzigmal das kleine Zeichen treffen muessen. Der
            ' Ablagekorb oeffnet sich weiter, er laesst sich nicht markieren.
            If vm.HasGroupSelection AndAlso Not entry.IsUnknownBin Then
                vm.ToggleGroupSelection(entry)
                e.Handled = True
                Return
            End If
            vm.OpenPerson(entry)
            ScrollToTop()
            e.Handled = True
        End Sub

        ''' <summary>Das Zeichen oben links auf einer Gruppenkachel: markieren oder die Markierung
        ''' wegnehmen, ohne die Gruppe zu oeffnen.</summary>
        Private Sub OnToggleGroupSelectionClick(sender As Object, e As RoutedEventArgs)
            Dim entry = TryCast(TryCast(sender, Button)?.DataContext, PersonListEntry)
            If entry Is Nothing Then Return
            TryCast(Me.DataContext, PeopleViewModel)?.ToggleGroupSelection(entry)
            e.Handled = True
        End Sub

        Private Sub OnSelectPageGroupsClick(sender As Object, e As RoutedEventArgs)
            TryCast(Me.DataContext, PeopleViewModel)?.SelectAllGroupsOnPage()
            e.Handled = True
        End Sub

        Private Sub OnClearGroupSelectionClick(sender As Object, e As RoutedEventArgs)
            TryCast(Me.DataContext, PeopleViewModel)?.ClearGroupSelection()
            e.Handled = True
        End Sub

        ''' <summary>Entfernt die markierten Gruppen nach Rueckfrage; die Frage nennt Gruppen und
        ''' Gesichter.</summary>
        Private Async Sub OnRemoveSelectedGroupsClick(sender As Object, e As RoutedEventArgs)
            e.Handled = True
            Dim vm = TryCast(Me.DataContext, PeopleViewModel)
            If vm Is Nothing Then Return
            Try
                Await vm.ConfirmAndRemoveSelectedGroupsAsync()
                ScrollToTop()
            Catch ex As Exception
                ' Eine Ausnahme in einem Async Sub landete sonst beim Dispatcher und beendete den
                ' Prozess.
                FerrumPix.Services.DiagnosticLogService.LogException("PeopleView.RemoveSelectedGroups", ex)
            End Try
        End Sub

        Private Sub OnAddSelectedGroupsClick(sender As Object, e As RoutedEventArgs)
            AddSelectedGroupsToPerson()
            e.Handled = True
        End Sub

        ''' <summary>Die Eingabetaste fuehrt zusammen wie der Knopf. Steht die Vorschlagsliste offen,
        ''' hat der Rahmen den ersten Druck fuer die Auswahl verbraucht, wie an jedem Namensfeld.</summary>
        Private Sub OnGroupTargetNameKeyDown(sender As Object, e As KeyEventArgs)
            If e.Key <> Key.Enter Then Return
            AddSelectedGroupsToPerson()
            e.Handled = True
        End Sub

        Private Sub AddSelectedGroupsToPerson()
            Dim box = Me.FindControl(Of AutoCompleteBox)("GroupTargetNameBox")
            Dim vm = TryCast(Me.DataContext, PeopleViewModel)
            If box Is Nothing OrElse vm Is Nothing Then Return
            If vm.AddSelectedGroupsToPerson(If(box.Text, "")) Then
                box.Text = ""
                ScrollToTop()
            End If
        End Sub

        Private Sub OnClosePersonClick(sender As Object, e As RoutedEventArgs)
            TryCast(Me.DataContext, PeopleViewModel)?.ClosePerson()
            ScrollToTop()
            e.Handled = True
        End Sub

        Private Sub OnPeoplePageClick(sender As Object, e As RoutedEventArgs)
            Dim richtung = TryCast(TryCast(sender, Button)?.Tag, String)
            Dim vm = TryCast(Me.DataContext, PeopleViewModel)
            If vm Is Nothing Then Return
            Select Case richtung
                Case "PeopleBack" : vm.ShowPeoplePage(vm.PeoplePage - 1)
                Case "PeopleForward" : vm.ShowPeoplePage(vm.PeoplePage + 1)
                Case "FaceBack" : vm.ShowFacePage(vm.FacePage - 1)
                Case "FaceForward" : vm.ShowFacePage(vm.FacePage + 1)
            End Select
            ScrollToTop()
            e.Handled = True
        End Sub

        ''' <summary>Der Name der geoeffneten Gruppe, uebernommen beim Verlassen des Feldes oder mit
        ''' der Eingabetaste - dieselbe Bedienung wie im Infopanel.
        '''
        ''' Ein vorhandener Name fuehrt die beiden Gruppen zusammen; das entscheidet die Bibliothek,
        ''' nicht die Ansicht.
        '''
        ''' NICHT uebernehmen, solange die Vorschlagsliste offen steht: ein Klick auf einen
        ''' Vorschlag nimmt dem Feld zuerst den Fokus, und ohne diese Sperre liefe die Uebernahme mit
        ''' dem Text von VOR dem Klick. Wer "Chr" tippt und "Christina" anklickt, benennt die Gruppe
        ''' sonst "Chr". Dieselbe Sperre steht in der Infoleiste, dort ausfuehrlich begruendet.</summary>
        Private Sub OnPersonNameCommitted(sender As Object, e As RoutedEventArgs)
            Dim box = TryCast(sender, AutoCompleteBox)
            If box Is Nothing Then Return
            ' EINEN DURCHLAUF SPAETER, wenn der neue Fokus feststeht, und dann nach der gemeinsamen
            ' Regel (PersonNameField): ein angeklickter Vorschlag gilt statt des getippten Texts,
            ' und solange die Liste offen ist oder der Fokus wieder im Feld steht, geschieht nichts.
            Avalonia.Threading.Dispatcher.UIThread.Post(
                Sub()
                    Dim suggestion As String = Nothing
                    Select Case PersonNameField.AfterLostFocus(box, suggestion)
                        Case PersonNameField.LostFocusAction.CommitSuggestion
                            box.Text = suggestion
                            CommitPersonName(box)
                        Case PersonNameField.LostFocusAction.CommitText
                            CommitPersonName(box)
                    End Select
                End Sub)
        End Sub

        ''' <summary>Die Eingabetaste uebernimmt. Steht die Vorschlagsliste offen, hat der Rahmen
        ''' die Taste schon fuer die Auswahl verbraucht und dieses Ereignis kommt gar nicht erst an -
        ''' der erste Druck waehlt dann den Vorschlag, der zweite uebernimmt ihn. Genau so soll es
        ''' sein: ein Vorschlag, den man versehentlich uebernimmt, benennt eine ganze Gruppe um.</summary>
        Private Sub OnPersonNameKeyDown(sender As Object, e As KeyEventArgs)
            If e.Key <> Key.Enter Then Return
            CommitPersonName(TryCast(sender, AutoCompleteBox))
            e.Handled = True
        End Sub

        Private Sub CommitPersonName(box As AutoCompleteBox)
            If box Is Nothing Then Return
            Dim vm = TryCast(Me.DataContext, PeopleViewModel)
            If vm Is Nothing OrElse vm.SelectedPerson Is Nothing Then Return
            If String.Equals(vm.SelectedPerson.Name, If(box.Text, ""), StringComparison.Ordinal) Then Return
            vm.RenameSelectedPerson(If(box.Text, ""))
        End Sub

        ''' <summary>Loest EIN Gesicht aus der geoeffneten Gruppe. Ueber das Tag der Schaltflaeche:
        ''' der Knopf sitzt in einer Vorlage, und eine Bindung ueber den Vorfahren findet von dort
        ''' aus den Befehl des ViewModels nicht zuverlaessig.</summary>
        Private Sub OnDetachPersonFaceClick(sender As Object, e As RoutedEventArgs)
            Dim faceId = TryCast(TryCast(sender, Button)?.Tag, String)
            If String.IsNullOrWhiteSpace(faceId) Then Return
            Dim vm = TryCast(Me.DataContext, PeopleViewModel)
            If vm Is Nothing Then Return
            vm.DetachFaceFromSelectedPerson(faceId)
            ' War es das letzte Gesicht, wirft das ViewModel die Gruppe weg und kehrt zur Wand
            ' zurueck - dann gehoert der Blick wieder an den Anfang.
            If vm.IsPeopleOverview Then ScrollToTop()
            e.Handled = True
        End Sub

    End Class

End Namespace
