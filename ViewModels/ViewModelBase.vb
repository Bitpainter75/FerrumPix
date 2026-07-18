Imports ReactiveUI

Namespace ViewModels

    Public MustInherit Class ViewModelBase
        Inherits ReactiveObject

        ' ── Schmales Fenster: Beschriftungen neben den Symbolen weglassen ──────────────
        ' Avalonia kennt keine Container-Queries, und ein Messen im Layout-Durchlauf würde
        ' erneut Layout auslösen. Deshalb EINE Quelle - die Fensterbreite in DIP, die
        ' MainWindow bei jeder Größenänderung meldet - und je Leiste eine eigene Schwelle.
        ' In DIP zu rechnen ist wichtig: die Anwendungsskalierung läuft über den globalen
        ' Skalierungsfaktor, wodurch die DIP-Breite bei größerer Skalierung von selbst
        ' schrumpft. Die Schwellen gelten dadurch für jede Skalierung.
        Private Shared _windowWidth As Double

        ''' <summary>Die zuletzt gemeldete Fensterbreite in DIP. 0 = noch nichts gemeldet.</summary>
        Protected Shared ReadOnly Property WindowWidth As Double
            Get
                Return _windowWidth
            End Get
        End Property

        Friend Shared Sub SetWindowWidth(width As Double)
            _windowWidth = width
        End Sub

        ''' <summary>Ab welcher Fensterbreite (DIP) die Leiste dieser Ansicht ihre Beschriftungen
        ''' behält. Jede Ansicht überschreibt das mit ihrem eigenen Platzbedarf - eine gemeinsame
        ''' Schwelle würde entweder im Editor zu spät oder im Betrachter zu früh greifen.</summary>
        Protected Overridable ReadOnly Property ToolbarLabelWidthThreshold As Double
            Get
                Return 1200
            End Get
        End Property

        ''' <summary>False, wenn die Leiste zu eng für Text neben den Symbolen ist. Solange noch
        ''' keine Breite gemeldet wurde (0), bleiben die Beschriftungen sichtbar - ein kurzes
        ''' Aufblitzen der Langfassung ist harmloser als eine dauerhaft nackte Symbolleiste,
        ''' falls die Meldung einmal ausbleibt.</summary>
        Public ReadOnly Property AreToolbarLabelsVisible As Boolean
            Get
                Return _windowWidth <= 0 OrElse _windowWidth >= ToolbarLabelWidthThreshold
            End Get
        End Property

        ''' <summary>Von MainWindowViewModel nach einer Breitenänderung gerufen.</summary>
        Friend Sub RaiseToolbarLabelsChanged()
            Me.RaisePropertyChanged(NameOf(AreToolbarLabelsVisible))
        End Sub

    End Class

    Public Enum AppMode
        Gallery = 0
        Viewer = 1
        Editor = 2
        Settings = 3
    End Enum

End Namespace
