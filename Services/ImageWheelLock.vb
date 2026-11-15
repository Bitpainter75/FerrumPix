Imports System

Namespace Services

    ''' <summary>Die Schalter "Mausrad ueber dem Bild abschalten" von Betrachter und Editor.
    '''
    ''' WOZU. Ueber dem Bild zoomt, blaettert oder aendert das Mausrad die Pinselgroesse. Wer ein
    ''' leichtgaengiges Rad oder ein Trackpad hat, loest das beim Arbeiten auch ungewollt aus. Der
    ''' Schalter in der Fusszeile nimmt dem Rad UEBER DEM BILD jede Wirkung. Alles daneben bleibt:
    ''' Leisten rollen, Regler unter dem Zeiger, der Filmstreifen.
    '''
    ''' JE ANSICHT EIN EIGENER STAND, gemerkt in den Einstellungen
    ''' (<see cref="AppSettings.ViewerWheelDisabled"/>, <see cref="AppSettings.EditorWheelDisabled"/>):
    ''' wer im Betrachter mit dem Rad blaettern, im Editor aber nicht versehentlich zoomen will,
    ''' stellt beides getrennt. Gelesen wird einmal und danach gemerkt, weil die Radwege der
    ''' Ansichten bei jeder Bewegung des Rades fragen.</summary>
    Public NotInheritable Class ImageWheelLock

        Private Sub New()
        End Sub

        Private Shared ReadOnly Gate As New Object()
        Private Shared _loaded As Boolean
        Private Shared _viewerDisabled As Boolean
        Private Shared _editorDisabled As Boolean

        Private Shared Sub EnsureLoaded()
            SyncLock Gate
                If _loaded Then Return
                Try
                    Dim settings = AppSettingsService.Load()
                    _viewerDisabled = settings.ViewerWheelDisabled
                    _editorDisabled = settings.EditorWheelDisabled
                Catch
                    _viewerDisabled = False
                    _editorDisabled = False
                End Try
                _loaded = True
            End SyncLock
        End Sub

        ''' <summary>Hat das Mausrad ueber dem Bild des Betrachters gerade keine Wirkung?</summary>
        Public Shared Property IsViewerDisabled As Boolean
            Get
                EnsureLoaded()
                Return _viewerDisabled
            End Get
            Set(value As Boolean)
                EnsureLoaded()
                If _viewerDisabled = value Then Return
                _viewerDisabled = value
                AppSettingsService.Update(Sub(s) s.ViewerWheelDisabled = value)
            End Set
        End Property

        ''' <summary>Hat das Mausrad ueber dem Bild des Editors gerade keine Wirkung?</summary>
        Public Shared Property IsEditorDisabled As Boolean
            Get
                EnsureLoaded()
                Return _editorDisabled
            End Get
            Set(value As Boolean)
                EnsureLoaded()
                If _editorDisabled = value Then Return
                _editorDisabled = value
                AppSettingsService.Update(Sub(s) s.EditorWheelDisabled = value)
            End Set
        End Property
    End Class

End Namespace
