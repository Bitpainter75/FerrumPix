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
        Private _isRenaming As Boolean

        Public Sub New(annotation As ImageAnnotation)
            Me.Annotation = annotation
        End Sub

        Public Sub New(layer As MaskedAdjustmentLayer)
            AdjustmentLayer = layer
        End Sub

        Public ReadOnly Property IsAdjustmentLayer As Boolean
            Get
                Return AdjustmentLayer IsNot Nothing
            End Get
        End Property

        Public ReadOnly Property LayerLabel As String
            Get
                If AdjustmentLayer IsNot Nothing Then
                    Return If(String.IsNullOrWhiteSpace(AdjustmentLayer.Name), LocalizationService.T("Lokale Korrektur"), AdjustmentLayer.Name)
                End If
                Return If(Annotation Is Nothing, "Ebene", Annotation.LayerLabel)
            End Get
        End Property

        Public Property EditableName As String
            Get
                If AdjustmentLayer IsNot Nothing Then Return If(AdjustmentLayer.Name, "")
                Return If(Annotation Is Nothing, "", Annotation.EditableName)
            End Get
            Set(value As String)
                If AdjustmentLayer IsNot Nothing Then
                    AdjustmentLayer.Name = If(value, "")
                ElseIf Annotation IsNot Nothing Then
                    Annotation.EditableName = value
                End If
                RaisePropertyChanged()
                RaisePropertyChanged(NameOf(LayerLabel))
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
                If AdjustmentLayer IsNot Nothing Then Return AdjustmentLayer.IsVisible
                Return Annotation IsNot Nothing AndAlso Annotation.IsVisible
            End Get
        End Property

        Public ReadOnly Property IconSource As String
            Get
                If AdjustmentLayer IsNot Nothing Then Return "avares://FerrumPix/Assets/Icons/outline/adjustments.svg"
                Return If(Annotation Is Nothing, "", Annotation.IconSource)
            End Get
        End Property

        Public Sub Refresh()
            RaisePropertyChanged(NameOf(LayerLabel))
            RaisePropertyChanged(NameOf(EditableName))
            RaisePropertyChanged(NameOf(IsVisible))
        End Sub

        Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged

        Private Sub RaisePropertyChanged(<CallerMemberName> Optional propertyName As String = Nothing)
            RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(propertyName))
        End Sub
    End Class

End Namespace
