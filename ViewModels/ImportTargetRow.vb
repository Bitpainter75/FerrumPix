Imports System
Imports System.Windows.Input
Imports FerrumPix.Services
Imports ReactiveUI

Namespace ViewModels

    ''' <summary>Eine Zeile der Liste "Zielordner für den Import" in den Einstellungen: Name und
    ''' Vorlage mit Platzhaltern. Jede Aenderung geht sofort in die Einstellungen, wie bei den
    ''' externen Programmen; die Textfelder melden erst beim Verlassen.</summary>
    Public Class ImportTargetRow
        Inherits ReactiveObject

        Private ReadOnly _id As String
        Private ReadOnly _save As Action
        Private _name As String
        Private _template As String

        Public Sub New(target As ImportTargetSettings, save As Action, remove As Action(Of ImportTargetRow))
            _id = If(String.IsNullOrWhiteSpace(target?.Id), Guid.NewGuid().ToString("N"), target.Id)
            _name = If(target?.Name, "")
            _template = If(target?.Template, "")
            _save = save
            RemoveCommand = ReactiveCommand.Create(Sub() remove?.Invoke(Me))
        End Sub

        Public Property Name As String
            Get
                Return _name
            End Get
            Set(value As String)
                Dim cleaned = If(value, "").Trim()
                If cleaned = _name Then Return
                Me.RaiseAndSetIfChanged(_name, cleaned)
                _save?.Invoke()
            End Set
        End Property

        Public Property Template As String
            Get
                Return _template
            End Get
            Set(value As String)
                Dim cleaned = If(value, "").Trim()
                If cleaned = _template Then Return
                Me.RaiseAndSetIfChanged(_template, cleaned)
                Me.RaisePropertyChanged(NameOf(PreviewText))
                _save?.Invoke()
            End Set
        End Property

        ''' <summary>Wie die Vorlage fuer eine Aufnahme von heute aufgeloest wuerde.</summary>
        Public ReadOnly Property PreviewText As String
            Get
                If String.IsNullOrWhiteSpace(_template) Then Return ""
                Dim sample As New ImportCandidate With {.CapturedAt = DateTime.Now, .ModifiedAt = DateTime.Now, .Camera = LocalizationService.T("Kamera")}
                Return String.Format(LocalizationService.T("Beispiel: {0}"), CardImportService.ExpandTargetTemplate(_template, sample))
            End Get
        End Property

        Public ReadOnly Property RemoveCommand As ICommand

        Public Function ToSettings() As ImportTargetSettings
            Return New ImportTargetSettings With {.Id = _id, .Name = _name, .Template = _template}
        End Function

    End Class

End Namespace
