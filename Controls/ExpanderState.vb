Imports Avalonia
Imports Avalonia.Controls
Imports FerrumPix.Services

Namespace Controls

    ''' <summary>Merkt sich den Auf-/Zuklapp-Zustand eines <see cref="Expander"/> dauerhaft in den
    ''' App-Einstellungen. Im XAML einfach <c>icons:ExpanderState.Key="details"</c> auf den Expander
    ''' setzen (der Schlüssel muss stabil und eindeutig sein - unabhängig vom Header-Text, damit auch
    ''' werkzeugabhängige Kopfzeilen funktionieren).
    '''
    ''' Beim Setzen des Schlüssels wird der gespeicherte Zustand angewandt (fehlt er, bleibt der Standard
    ''' = aufgeklappt). Jede spätere Änderung von <see cref="Expander.IsExpanded"/> wird über den
    ''' gebündelten Speicher-Pfad von <see cref="AppSettingsService"/> persistiert. Bewusst NICHT über das
    ''' ViewModel: der Zustand ist reine Bedien-Erinnerung wie Info-Leiste/Ebenen-Panel und geht direkt
    ''' zum Einstellungs-Speicher.</summary>
    Public NotInheritable Class ExpanderState

        Private Sub New()
        End Sub

        ''' Setzt der Lade-Vorgang IsExpanded, darf die Änderung NICHT sofort wieder gespeichert werden.
        ''' Der UI-Thread ist einläufig, daher genügt ein einfaches Flag.
        Private Shared _applying As Boolean

        Public Shared ReadOnly KeyProperty As AttachedProperty(Of String) =
            AvaloniaProperty.RegisterAttached(Of ExpanderState, Expander, String)("Key")

        Public Shared Function GetKey(target As Expander) As String
            Return target.GetValue(KeyProperty)
        End Function

        Public Shared Sub SetKey(target As Expander, value As String)
            target.SetValue(KeyProperty, value)
        End Sub

        ''' <summary>Der Stand ohne gemerkten Wert. Ab Werk aufgeklappt; eine Gruppe, die man meist
        ''' nicht braucht (Füllen und Kontur im Auswahl-Werkzeug), setzt False.</summary>
        Public Shared ReadOnly DefaultExpandedProperty As AttachedProperty(Of Boolean) =
            AvaloniaProperty.RegisterAttached(Of ExpanderState, Expander, Boolean)("DefaultExpanded", True)

        Public Shared Function GetDefaultExpanded(target As Expander) As Boolean
            Return target.GetValue(DefaultExpandedProperty)
        End Function

        Public Shared Sub SetDefaultExpanded(target As Expander, value As Boolean)
            target.SetValue(DefaultExpandedProperty, value)
        End Sub

        ''' <summary>Eine Gruppe mit Schalter "Aktiv" (Füllung, Kontur, Schatten, Glühen am Objekt):
        ''' gebunden an diesen Schalter, ist sie zu, solange er aus ist, und geht auf, sobald er an
        ''' ist - auch beim Wechsel zu einem anderen Objekt. Nothing gibt die Gruppe an den gemerkten
        ''' Zustand zurück (dieselbe Gruppe im Auswahl-Werkzeug, wo es keinen Schalter gibt). Solange
        ''' sie folgt, wird ein Klick auf den Kopf nicht gemerkt: der nächste Wechsel stellt sie
        ''' ohnehin wieder nach dem Schalter.</summary>
        Public Shared ReadOnly ExpandWhenProperty As AttachedProperty(Of Boolean?) =
            AvaloniaProperty.RegisterAttached(Of ExpanderState, Expander, Boolean?)("ExpandWhen")

        Public Shared Function GetExpandWhen(target As Expander) As Boolean?
            Return target.GetValue(ExpandWhenProperty)
        End Function

        Public Shared Sub SetExpandWhen(target As Expander, value As Boolean?)
            target.SetValue(ExpandWhenProperty, value)
        End Sub

        Shared Sub New()
            KeyProperty.Changed.AddClassHandler(Of Expander)(AddressOf OnKeyChanged)
            DefaultExpandedProperty.Changed.AddClassHandler(Of Expander)(AddressOf OnKeyChanged)
            ExpandWhenProperty.Changed.AddClassHandler(Of Expander)(AddressOf OnKeyChanged)
            Expander.IsExpandedProperty.Changed.AddClassHandler(Of Expander)(AddressOf OnIsExpandedChanged)
        End Sub

        ''' <summary>Der gemerkte Stand, sonst der Standard der Gruppe.</summary>
        Private Shared Function SavedOrDefault(expander As Expander, key As String) As Boolean
            Dim states = AppSettingsService.Load().EditorExpanderStates
            If states IsNot Nothing AndAlso states.ContainsKey(key) Then Return states(key)
            Return GetDefaultExpanded(expander)
        End Function

        ''' <summary>Laeuft bei jeder der drei Angaben (Schluessel, Standard, Schalter): die Reihenfolge,
        ''' in der das XAML sie setzt, ist nicht festgelegt, und erst alle zusammen ergeben den Stand.</summary>
        Private Shared Sub OnKeyChanged(expander As Expander, e As AvaloniaPropertyChangedEventArgs)
            Dim follow = GetExpandWhen(expander)
            If follow.HasValue Then
                ApplyWithoutSaving(expander, follow.Value)
                Return
            End If
            Dim key = GetKey(expander)
            If String.IsNullOrEmpty(key) Then Return
            ' Standard = aufgeklappt, ausser die Gruppe sagt anderes (DefaultExpanded); ein gespeicherter
            ' Wert geht vor. Das XAML setzt IsExpanded bewusst nicht, damit Standard und gemerkter
            ' Zustand nicht kollidieren.
            Dim saved = SavedOrDefault(expander, key)
            ' Entsteht eine Gruppe, waehrend der Kompaktmodus sie verwaltet, gilt SEIN Stand.
            If SuspendedKeys.Contains(key) AndAlso CompactStateOf IsNot Nothing Then saved = CompactStateOf(key)
            ApplyWithoutSaving(expander, saved)
        End Sub

        ''' <summary>Der Stand des Kompaktmodus fuer einen seiner Schluessel, solange er seine Gruppen
        ''' verwaltet (<see cref="SuspendedKeys"/>). Nothing ausserhalb davon.</summary>
        Public Shared Property CompactStateOf As Func(Of String, Boolean) = Nothing

        ''' <summary>Wohin ein Klick auf eine Gruppe des Kompaktmodus gespeichert wird. Nothing: gar
        ''' nicht, dann merkt sich der Modus selbst, was offen ist (mit automatischem Zuklappen nur
        ''' die eine Gruppe, AppSettings.EditorCompactOpenGroup).</summary>
        Public Shared Property CompactSave As Action(Of String, Boolean) = Nothing

        Private Shared Sub OnIsExpandedChanged(expander As Expander, e As AvaloniaPropertyChangedEventArgs)
            If _applying Then Return
            If GetExpandWhen(expander).HasValue Then Return
            Dim key = GetKey(expander)
            If String.IsNullOrEmpty(key) Then Return
            If SuspendedKeys.Contains(key) Then
                CompactSave?.Invoke(key, expander.IsExpanded)
                Return
            End If
            AppSettingsService.SaveEditorExpanderState(key, expander.IsExpanded)
        End Sub

        ''' <summary>Schluessel, deren Auf- und Zuklappen gerade NICHT in den Zustand der
        ''' Einzelwerkzeuge geht. Der Kompaktmodus der Anpassungen traegt hier seine Gruppen ein,
        ''' solange sein Werkzeug offen ist: es sind dieselben Expander wie in den Einzelwerkzeugen,
        ''' und sein Auf- und Zuklappen soll dort nichts veraendern. Er merkt sich seinen Stand
        ''' selbst (<see cref="CompactStateOf"/>, <see cref="CompactSave"/>).</summary>
        Public Shared ReadOnly SuspendedKeys As New HashSet(Of String)(StringComparer.Ordinal)

        ''' <summary>Setzt gerade der Code den Zustand, nicht der Nutzer? Das Ereignis Expanding kommt
        ''' in beiden Faellen, und wer nur auf den Klick antworten will, fragt hier.</summary>
        Public Shared ReadOnly Property IsApplying As Boolean
            Get
                Return _applying
            End Get
        End Property

        ''' <summary>Auf- oder zuklappen, ohne dass es gespeichert wird.</summary>
        Public Shared Sub ApplyWithoutSaving(expander As Expander, expanded As Boolean)
            If expander Is Nothing OrElse expander.IsExpanded = expanded Then Return
            Dim wasApplying = _applying
            _applying = True
            Try
                expander.IsExpanded = expanded
            Finally
                _applying = wasApplying
            End Try
        End Sub

        ''' <summary>Den gespeicherten Zustand wieder anwenden, wie beim ersten Setzen des Schluessels
        ''' (Standard aufgeklappt). Fuer den Weg aus dem Kompaktmodus zurueck in ein Einzelwerkzeug.</summary>
        Public Shared Sub RestoreSaved(expander As Expander)
            If expander Is Nothing Then Return
            Dim follow = GetExpandWhen(expander)
            If follow.HasValue Then
                ApplyWithoutSaving(expander, follow.Value)
                Return
            End If
            Dim key = GetKey(expander)
            If String.IsNullOrEmpty(key) Then Return
            ApplyWithoutSaving(expander, SavedOrDefault(expander, key))
        End Sub

    End Class

End Namespace
