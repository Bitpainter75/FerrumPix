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

        Shared Sub New()
            KeyProperty.Changed.AddClassHandler(Of Expander)(AddressOf OnKeyChanged)
            Expander.IsExpandedProperty.Changed.AddClassHandler(Of Expander)(AddressOf OnIsExpandedChanged)
        End Sub

        Private Shared Sub OnKeyChanged(expander As Expander, e As AvaloniaPropertyChangedEventArgs)
            Dim key = GetKey(expander)
            If String.IsNullOrEmpty(key) Then Return
            ' Standard = aufgeklappt (nur ein gespeicherter Wert klappt eine Gruppe zu). Das XAML setzt
            ' IsExpanded bewusst nicht mehr, damit dieser Standard und der gemerkte Zustand nicht kollidieren.
            Dim states = AppSettingsService.Load().EditorExpanderStates
            Dim saved As Boolean = True
            If states IsNot Nothing AndAlso states.ContainsKey(key) Then saved = states(key)
            ' Entsteht eine Gruppe, waehrend der Kompaktmodus sie verwaltet, gilt SEIN Stand.
            If SuspendedKeys.Contains(key) AndAlso CompactStateOf IsNot Nothing Then saved = CompactStateOf(key)
            _applying = True
            Try
                expander.IsExpanded = saved
            Finally
                _applying = False
            End Try
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
            Dim key = GetKey(expander)
            If String.IsNullOrEmpty(key) Then Return
            Dim states = AppSettingsService.Load().EditorExpanderStates
            Dim saved As Boolean = True
            If states IsNot Nothing AndAlso states.ContainsKey(key) Then saved = states(key)
            ApplyWithoutSaving(expander, saved)
        End Sub

    End Class

End Namespace
