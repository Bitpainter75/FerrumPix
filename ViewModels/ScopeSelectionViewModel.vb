Imports System
Imports System.Windows.Input
Imports ReactiveUI
Imports FerrumPix.Services

Namespace ViewModels

    ''' <summary>Die Wahl der Bildanalyse im Infopanel: Histogramm, Waveform oder RGB-Parade.
    '''
    ''' Eigene kleine Klasse und nicht dreimal dieselben Eigenschaften: das Panel ist EIN
    ''' Steuerelement mit drei Datenkontexten (siehe <see cref="IInfoSidebarPanel"/>), und jede
    ''' Eigenschaft, die es braucht, muesste sonst in Galerie, Betrachter und Editor einzeln stehen.
    ''' So halten alle drei dieselbe Instanzenart unter dem Namen "Scope", und die Bindung lautet
    ''' ueberall gleich.
    '''
    ''' <para>Die Wahl selbst ist ANWENDUNGSWEIT und liegt deshalb statisch. Sie an der Instanz zu
    ''' halten war falsch: Galerie, Betrachter und Editor bekommen je eine, alle drei entstehen beim
    ''' Programmstart und lasen die Einstellung genau einmal. Wer danach umschaltete, aenderte nur
    ''' seine eigene - die anderen beiden blieben stehen, und beim Wechsel der Ansicht passte die
    ''' Knopfmarkierung nicht mehr zum gezeichneten Bild (gemeldet 2026-08-15).</para>
    '''
    ''' <para>Das Ereignis darunter haelt die Instanzen am Leben, solange die Anwendung laeuft. Das
    ''' ist hier unbedenklich und Absicht: es gibt genau drei, alle im MainWindowViewModel angelegt
    ''' und so langlebig wie das Fenster. Wer eine vierte, kurzlebige anlegt, muss sich abmelden.</para></summary>
    Public NotInheritable Class ScopeSelectionViewModel
        Inherits ViewModelBase

        ''' <summary>Die eine Wahl. Beim ersten Zugriff aus den Einstellungen, danach hier.</summary>
        Private Shared _sharedMode As String =
            AppSettingsService.NormalizeScopeMode(AppSettingsService.Load().ScopeMode)

        ''' <summary>Meldet jeder Instanz, dass die Wahl gewechselt hat - auch der, die es
        ''' ausgeloest hat. So laeuft fuer alle derselbe Weg.</summary>
        Private Shared Event SharedModeChanged As EventHandler

        Private Shared _generation As Integer

        ''' <summary>Zaehlt jede Aenderung der Wahl mit. Wer ein Analysebild im Hintergrund rechnet,
        ''' merkt sich den Stand beim Start und verwirft sein Ergebnis, wenn es beim Eintreffen nicht
        ''' mehr stimmt.
        '''
        ''' Ohne diesen Zaehler gewann der langsamere Lauf: der Betrachter prueft beim Eintreffen
        ''' sonst nur, ob noch DASSELBE BILD gilt - und das gilt beim Umschalten der Darstellung ja
        ''' gerade. Ein alter Lauf schrieb damit sein Histogramm ueber die eben angeforderte
        ''' Waveform und setzte zugleich den Merker "fuer diesen Pfad erledigt", womit auch kein
        ''' weiterer Versuch mehr kam.</summary>
        Public Shared ReadOnly Property Generation As Integer
            Get
                Return Threading.Volatile.Read(_generation)
            End Get
        End Property

        ''' <summary>Wird gerufen, wenn sich die Wahl geaendert hat. Der Besitzer verwirft daraufhin
        ''' sein Analysebild und rechnet neu - WIE, weiss nur er: der Editor nimmt die Szene, die
        ''' anderen beiden die Datei.</summary>
        Private ReadOnly _onChanged As Action

        Public Sub New(onChanged As Action)
            _onChanged = onChanged
            SetModeCommand = New DelegateCommand(Sub(parameter) Mode = TryCast(parameter, String))
            AddHandler SharedModeChanged, AddressOf OnSharedModeChanged
        End Sub

        ''' <summary>"Histogram", "Waveform" oder "Parade".</summary>
        Public Property Mode As String
            Get
                Return _sharedMode
            End Get
            Set(value As String)
                Dim normalized = AppSettingsService.NormalizeScopeMode(value)
                If String.Equals(_sharedMode, normalized, StringComparison.Ordinal) Then Return
                _sharedMode = normalized
                AppSettingsService.SaveScopeMode(normalized)
                Threading.Interlocked.Increment(_generation)
                ' Erst schreiben, dann melden: der Renderweg liest die Einstellung
                ' (ImageProcessor.RenderScope), und er darf sie nie aelter vorfinden als die Knoepfe.
                RaiseEvent SharedModeChanged(Nothing, EventArgs.Empty)
            End Set
        End Property

        Private Sub OnSharedModeChanged(sender As Object, e As EventArgs)
            Me.RaisePropertyChanged(NameOf(Mode))
            Me.RaisePropertyChanged(NameOf(IsHistogram))
            Me.RaisePropertyChanged(NameOf(IsWaveform))
            Me.RaisePropertyChanged(NameOf(IsParade))
            _onChanged?.Invoke()
        End Sub

        Public ReadOnly Property IsHistogram As Boolean
            Get
                Return _sharedMode = "Histogram"
            End Get
        End Property

        Public ReadOnly Property IsWaveform As Boolean
            Get
                Return _sharedMode = "Waveform"
            End Get
        End Property

        Public ReadOnly Property IsParade As Boolean
            Get
                Return _sharedMode = "Parade"
            End Get
        End Property

        Public ReadOnly Property SetModeCommand As ICommand

    End Class

End Namespace
