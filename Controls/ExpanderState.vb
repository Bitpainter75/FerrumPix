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
            _applying = True
            Try
                expander.IsExpanded = saved
            Finally
                _applying = False
            End Try
        End Sub

        Private Shared Sub OnIsExpandedChanged(expander As Expander, e As AvaloniaPropertyChangedEventArgs)
            If _applying Then Return
            Dim key = GetKey(expander)
            If String.IsNullOrEmpty(key) Then Return
            AppSettingsService.SaveEditorExpanderState(key, expander.IsExpanded)
        End Sub

    End Class

End Namespace
