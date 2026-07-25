Imports System.ComponentModel
Imports System.Runtime.CompilerServices
Imports FerrumPix.Services

Namespace ViewModels

    ''' <summary>Gemeinsamer UI-Eintrag für Objekt- und lokale Einstellungsebenen.
    ''' Die Renderdaten bleiben in ihren spezialisierten Modellen; das Panel braucht nur diese dünne Sicht.</summary>
    Public NotInheritable Class LayerPanelRow
        Implements INotifyPropertyChanged

        Public ReadOnly Property Annotation As ImageAnnotation
        Public ReadOnly Property AdjustmentLayer As MaskedAdjustmentLayer
        ''' <summary>Gesetzt bei der KOPFZEILE einer Objekt-Gruppe. Ihre Mitglieder stehen als eigene
        ''' Zeilen darunter (eingerückt) und tragen dieselbe Gruppe in <see cref="MemberOfGroup"/>.</summary>
        Public ReadOnly Property Group As AnnotationGroup
        ''' <summary>Gesetzt bei einer Mitgliedszeile - für die Einrückung und dafür, dass ein Klick auf
        ''' das Mitglied weiß, zu welcher Gruppe es gehört.</summary>
        Public ReadOnly Property MemberOfGroup As AnnotationGroup
        Private _isRenaming As Boolean

        Public Sub New(annotation As ImageAnnotation, Optional memberOfGroup As AnnotationGroup = Nothing)
            Me.Annotation = annotation
            Me.MemberOfGroup = memberOfGroup
        End Sub

        Public Sub New(layer As MaskedAdjustmentLayer, Optional memberOfGroup As AnnotationGroup = Nothing)
            AdjustmentLayer = layer
            Me.MemberOfGroup = memberOfGroup
        End Sub

        Public Sub New(group As AnnotationGroup)
            Me.Group = group
        End Sub

        Public ReadOnly Property IsAdjustmentLayer As Boolean
            Get
                Return AdjustmentLayer IsNot Nothing
            End Get
        End Property

        Public ReadOnly Property IsGroupHeader As Boolean
            Get
                Return Group IsNot Nothing
            End Get
        End Property

        Public ReadOnly Property IsGroupMember As Boolean
            Get
                Return MemberOfGroup IsNot Nothing
            End Get
        End Property

        ''' <summary>Linker Einzug der Zeile: Mitglieder stehen sichtbar unter ihrer Kopfzeile.</summary>
        Public ReadOnly Property IndentMargin As Avalonia.Thickness
            Get
                Return New Avalonia.Thickness(If(IsGroupMember, 16, 0), 0, 0, 0)
            End Get
        End Property

        ''' <summary>Pfeil der Kopfzeile: zeigt nach unten, wenn die Gruppe offen ist. Als Symbol aus
        ''' demselben Satz wie der Rest der Oberfläche - ein Textglyph fällt daneben aus dem Rahmen.</summary>
        Public ReadOnly Property GroupToggleIconSource As String
            Get
                If Group Is Nothing Then Return ""
                Return If(Group.IsCollapsed,
                          "avares://FerrumPix/Assets/Icons/outline/caret-right.svg",
                          "avares://FerrumPix/Assets/Icons/outline/caret-down.svg")
            End Get
        End Property

        Public ReadOnly Property LayerLabel As String
            Get
                If Group IsNot Nothing Then
                    Return If(String.IsNullOrWhiteSpace(Group.Name), LocalizationService.T("Gruppe"), Group.Name)
                End If
                If AdjustmentLayer IsNot Nothing Then
                    If Not String.IsNullOrWhiteSpace(AdjustmentLayer.Name) Then Return AdjustmentLayer.Name
                    Return If(AdjustmentLayer.IsMaskLayer, LocalizationService.T("Masken-Korrektur"), LocalizationService.T("Auswahl-Korrektur"))
                End If
                Return If(Annotation Is Nothing, "Ebene", Annotation.LayerLabel)
            End Get
        End Property

        Public Property EditableName As String
            Get
                If Group IsNot Nothing Then Return If(Group.Name, "")
                If AdjustmentLayer IsNot Nothing Then Return If(AdjustmentLayer.Name, "")
                Return If(Annotation Is Nothing, "", Annotation.EditableName)
            End Get
            Set(value As String)
                If Group IsNot Nothing Then
                    Group.Name = If(value, "")
                ElseIf AdjustmentLayer IsNot Nothing Then
                    AdjustmentLayer.Name = If(value, "")
                ElseIf Annotation IsNot Nothing Then
                    Annotation.EditableName = value
                End If
                RaisePropertyChanged()
                RaisePropertyChanged(NameOf(LayerLabel))
            End Set
        End Property

        Private _isInMultiSelection As Boolean

        ''' <summary>Gehört diese Zeile zu einer MEHRFACHauswahl? Die ListBox markiert nur eine Zeile;
        ''' ohne diese Kennzeichnung sähe eine Mehrfachauswahl im Panel aus, als wäre sie nicht
        ''' zustande gekommen.</summary>
        Public Property IsInMultiSelection As Boolean
            Get
                Return _isInMultiSelection
            End Get
            Set(value As Boolean)
                If _isInMultiSelection = value Then Return
                _isInMultiSelection = value
                RaisePropertyChanged()
            End Set
        End Property

        Public Property IsRenaming As Boolean
            Get
                Return _isRenaming
            End Get
            Set(value As Boolean)
                If _isRenaming = value Then Return
                _isRenaming = value
                RaisePropertyChanged()
            End Set
        End Property

        Public ReadOnly Property IsVisible As Boolean
            Get
                If Group IsNot Nothing Then Return Group.IsVisible
                If AdjustmentLayer IsNot Nothing Then Return AdjustmentLayer.IsVisible
                Return Annotation IsNot Nothing AndAlso Annotation.IsVisible
            End Get
        End Property

        ''' <summary>Gesperrt? Ein Objekt gilt auch dann als gesperrt, wenn seine GRUPPE es ist -
        ''' das Schloss in der Mitgliedszeile zeigt dann denselben Zustand wie die Kopfzeile.</summary>
        Public ReadOnly Property IsLocked As Boolean
            Get
                If Group IsNot Nothing Then Return Group.IsLocked
                If MemberOfGroup IsNot Nothing AndAlso MemberOfGroup.IsLocked Then Return True
                If AdjustmentLayer IsNot Nothing Then Return AdjustmentLayer.IsLocked
                Return Annotation IsNot Nothing AndAlso Annotation.IsLocked
            End Get
        End Property

        ''' <summary>Das Schloss wird nur bei GESPERRTEN Zeilen gezeichnet - offen bliebe es sonst als
        ''' Dauergast in jeder Zeile stehen und würde das Auge verwässern.</summary>
        Public ReadOnly Property LockIconSource As String
            Get
                Return If(IsLocked,
                          "avares://FerrumPix/Assets/Icons/outline/lock.svg",
                          "avares://FerrumPix/Assets/Icons/outline/lock-open.svg")
            End Get
        End Property

        Public ReadOnly Property IconSource As String
            Get
                If Group IsNot Nothing Then Return "avares://FerrumPix/Assets/Icons/outline/folder.svg"
                ' Art-abhängiges Symbol: MASKEN-Ebene = Masken-Symbol (rotes Overlay im Bild), AUSWAHL-Ebene
                ' = Laufameisen-Rechteck (Ameisen im Bild). So sind die beiden im Panel unterscheidbar - und
                ' von der globalen Bildanpassungen-Zeile (Regler-Symbol) und Objekt-Ebenen.
                If AdjustmentLayer IsNot Nothing Then
                    Return If(AdjustmentLayer.IsMaskLayer,
                              "avares://FerrumPix/Assets/Icons/outline/mask.svg",
                              "avares://FerrumPix/Assets/Icons/outline/marquee.svg")
                End If
                Return If(Annotation Is Nothing, "", Annotation.IconSource)
            End Get
        End Property

        Public Sub Refresh()
            RaisePropertyChanged(NameOf(LayerLabel))
            RaisePropertyChanged(NameOf(EditableName))
            RaisePropertyChanged(NameOf(IsVisible))
            RaisePropertyChanged(NameOf(IsLocked))
            RaisePropertyChanged(NameOf(LockIconSource))
            RaisePropertyChanged(NameOf(GroupToggleIconSource))
        End Sub

        Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged

        Private Sub RaisePropertyChanged(<CallerMemberName> Optional propertyName As String = Nothing)
            RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(propertyName))
        End Sub
    End Class

End Namespace
