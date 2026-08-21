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

        ''' <summary>Die beiden ORTE haben je eine eigene Wahl. Das ist der Sinn der Sache: wer das
        ''' Bild an zwei Stellen sieht, will dort meist zwei verschiedene Fragen beantwortet haben -
        ''' die Verteilung in der Leiste, die Kanaltrennung neben den Reglern. Innerhalb EINES Ortes
        ''' gilt die Wahl weiterhin anwendungsweit: Galerie, Betrachter und Editor teilen sich ihre
        ''' Leiste, sonst zeigte derselbe Kasten je nach Ansicht etwas anderes.</summary>
        Private Shared _sharedMode As String =
            AppSettingsService.NormalizeScopeMode(AppSettingsService.Load().ScopeMode)
        Private Shared _panelMode As String =
            AppSettingsService.NormalizeScopeMode(AppSettingsService.Load().ScopePanelMode)

        ''' <summary>Welcher der beiden Orte. Der Kanal steht am Konstruktor und aendert sich
        ''' nicht - eine Instanz gehoert zu einem Kasten auf dem Schirm.</summary>
        Public Enum ScopeChannel
            InfoSidebar = 0
            AdjustmentPanel = 1
        End Enum

        Private ReadOnly _channel As ScopeChannel

        ''' <summary>Meldet die Änderung genau an die Instanzen DESSELBEN Orts. Die Leisten
        ''' (Galerie, Betrachter, Editor) teilen sich ihre Wahl; das Anpassungspanel ist dagegen
        ''' ein eigener Ort und darf nicht beim Umschalten der Leiste sein Bild verwerfen.
        ''' Der Kanal wird mitgegeben, statt zwei fast gleiche Ereignisse zu pflegen.</summary>
        Private Shared Event SharedModeChanged(channel As ScopeChannel)

        ' Hintergrundläufe für die Leiste prüfen Generation. Das Panel rendert auf dem UI-Thread,
        ' bekommt aber seinen eigenen Zähler, damit eine spätere asynchrone Quelle denselben klaren
        ' Vertrag hat und ein Panel-Klick keinen Leistenlauf ungültig macht.
        Private Shared _sidebarGeneration As Integer
        Private Shared _panelGeneration As Integer

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
                Return Threading.Volatile.Read(_sidebarGeneration)
            End Get
        End Property

        Public Shared ReadOnly Property PanelGeneration As Integer
            Get
                Return Threading.Volatile.Read(_panelGeneration)
            End Get
        End Property

        ''' <summary>Wird gerufen, wenn sich die Wahl geaendert hat. Der Besitzer verwirft daraufhin
        ''' sein Analysebild und rechnet neu - WIE, weiss nur er: der Editor nimmt die Szene, die
        ''' anderen beiden die Datei.</summary>
        Private ReadOnly _onChanged As Action

        Public Sub New(onChanged As Action, Optional channel As ScopeChannel = ScopeChannel.InfoSidebar)
            _onChanged = onChanged
            _channel = channel
            SetModeCommand = New DelegateCommand(Sub(parameter) Mode = TryCast(parameter, String))
            AddHandler SharedModeChanged, AddressOf OnSharedModeChanged
        End Sub

        ''' <summary>"Histogram", "Waveform" oder "Parade" - die Wahl DIESES Ortes.</summary>
        Public Property Mode As String
            Get
                Return If(_channel = ScopeChannel.AdjustmentPanel, _panelMode, _sharedMode)
            End Get
            Set(value As String)
                Dim normalized = AppSettingsService.NormalizeScopeMode(value)
                If String.Equals(Mode, normalized, StringComparison.Ordinal) Then Return
                If _channel = ScopeChannel.AdjustmentPanel Then
                    _panelMode = normalized
                    AppSettingsService.SaveScopePanelMode(normalized)
                Else
                    _sharedMode = normalized
                    AppSettingsService.SaveScopeMode(normalized)
                End If
                If _channel = ScopeChannel.AdjustmentPanel Then
                    Threading.Interlocked.Increment(_panelGeneration)
                Else
                    Threading.Interlocked.Increment(_sidebarGeneration)
                End If
                ' Erst schreiben, dann melden: der Renderweg bekommt die Wahl als Parameter, und er
                ' darf sie nie aelter vorfinden als die Knoepfe.
                RaiseEvent SharedModeChanged(_channel)
            End Set
        End Property

        ''' <summary>Die Wahl des Anpassungspanels, fuer die Rechenwege. Statisch, weil der
        ''' Editor sie braucht, ohne eine Instanz in der Hand zu haben.</summary>
        Public Shared ReadOnly Property PanelMode As String
            Get
                Return _panelMode
            End Get
        End Property

        ''' <summary>Die Wahl der Infoleiste, fuer die Rechenwege.</summary>
        Public Shared ReadOnly Property SidebarMode As String
            Get
                Return _sharedMode
            End Get
        End Property

        Private Sub OnSharedModeChanged(changedChannel As ScopeChannel)
            If changedChannel <> _channel Then Return
            Me.RaisePropertyChanged(NameOf(Mode))
            Me.RaisePropertyChanged(NameOf(IsHistogram))
            Me.RaisePropertyChanged(NameOf(IsWaveform))
            Me.RaisePropertyChanged(NameOf(IsParade))
            _onChanged?.Invoke()
        End Sub

        Public ReadOnly Property IsHistogram As Boolean
            Get
                Return Mode = "Histogram"
            End Get
        End Property

        Public ReadOnly Property IsWaveform As Boolean
            Get
                Return Mode = "Waveform"
            End Get
        End Property

        Public ReadOnly Property IsParade As Boolean
            Get
                Return Mode = "Parade"
            End Get
        End Property

        Public ReadOnly Property SetModeCommand As ICommand

        ''' <summary>WO das Analysebild steht - genau wie die Wahl der Darstellung anwendungsweit
        ''' und deshalb statisch. Aus demselben Grund: alle Instanzen entstehen beim Programmstart,
        ''' und eine Einstellung, die jede fuer sich einmal liest, laeuft nach dem ersten Umschalten
        ''' auseinander.
        '''
        ''' Gelesen wird das hier und nicht bei jedem Bindungszugriff aus den Einstellungen: die
        ''' Sichtbarkeit wird pro Bild und pro Panel abgefragt, und jede Abfrage waere sonst ein
        ''' Deserialisieren der ganzen Einstellungsdatei.</summary>
        Private Shared _showInInfoSidebar As Boolean = AppSettingsService.Load().ScopeInInfoSidebar
        Private Shared _showInAdjustmentPanels As Boolean = AppSettingsService.Load().ScopeInAdjustmentPanels

        Public Shared ReadOnly Property ShowInInfoSidebar As Boolean
            Get
                Return _showInInfoSidebar
            End Get
        End Property

        Public Shared ReadOnly Property ShowInAdjustmentPanels As Boolean
            Get
                Return _showInAdjustmentPanels
            End Get
        End Property

        ''' <summary>Wird nirgends sonst gebraucht? Dann steht auch kein Rechenweg mehr an. Die
        ''' Besitzer fragen genau das, bevor sie ein Analysebild anfordern.</summary>
        Public Shared ReadOnly Property IsShownAnywhere As Boolean
            Get
                Return _showInInfoSidebar OrElse _showInAdjustmentPanels
            End Get
        End Property

        ''' <summary>Uebernimmt die im Einstellungsdialog gewaehlte Platzierung. Das Speichern macht
        ''' der Aufrufer - hier steht nur der Stand, den die Oberflaeche liest.
        '''
        ''' ZAEHLT MIT, genau wie ein Wechsel der Darstellung: ein Lauf, der beim Abschalten schon
        ''' unterwegs war, hat seinen Decode zwar bezahlt, darf sein Ergebnis aber nicht mehr
        ''' abliefern - sonst haengt eine Bitmap im Speicher fuer etwas, das niemand mehr sieht.
        ''' Die Rechenwege merken sich den Stand beim Start und vergleichen beim Eintreffen.</summary>
        Public Shared Sub ApplyPlacement(inInfoSidebar As Boolean, inAdjustmentPanels As Boolean)
            If _showInInfoSidebar = inInfoSidebar AndAlso _showInAdjustmentPanels = inAdjustmentPanels Then Return
            Dim sidebarChanged = _showInInfoSidebar <> inInfoSidebar
            Dim panelChanged = _showInAdjustmentPanels <> inAdjustmentPanels
            _showInInfoSidebar = inInfoSidebar
            _showInAdjustmentPanels = inAdjustmentPanels
            If sidebarChanged Then Threading.Interlocked.Increment(_sidebarGeneration)
            If panelChanged Then Threading.Interlocked.Increment(_panelGeneration)
        End Sub

    End Class

End Namespace
