Imports System
Imports System.Windows.Input
Imports FerrumPix.Services
Imports ReactiveUI

Namespace ViewModels

    ''' <summary>Eine Zeile der Liste "Öffnen mit" in den Einstellungen. Jede Aenderung geht sofort
    ''' in die Einstellungen, wie bei den uebrigen Feldern der Seite; die Textfelder melden erst beim
    ''' Verlassen, damit nicht jeder Tastendruck die Datei schreibt.</summary>
    Public Class OpenWithProgramRow
        Inherits ReactiveObject

        Private ReadOnly _id As String
        Private ReadOnly _save As Action
        Private _name As String
        Private _programPath As String
        Private _arguments As String

        Public Sub New(program As OpenWithProgramSettings, save As Action, remove As Action(Of OpenWithProgramRow))
            _id = If(String.IsNullOrWhiteSpace(program?.Id), Guid.NewGuid().ToString("N"), program.Id)
            _name = If(program?.Name, "")
            _programPath = If(program?.ProgramPath, "")
            _arguments = If(program?.Arguments, "")
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

        Public Property ProgramPath As String
            Get
                Return _programPath
            End Get
            Set(value As String)
                Dim cleaned = If(value, "").Trim()
                If cleaned = _programPath Then Return
                Me.RaiseAndSetIfChanged(_programPath, cleaned)
                _save?.Invoke()
            End Set
        End Property

        Public Property Arguments As String
            Get
                Return _arguments
            End Get
            Set(value As String)
                Dim cleaned = If(value, "").Trim()
                If cleaned = _arguments Then Return
                Me.RaiseAndSetIfChanged(_arguments, cleaned)
                _save?.Invoke()
            End Set
        End Property

        Public ReadOnly Property RemoveCommand As ICommand

        Public Function ToSettings() As OpenWithProgramSettings
            Return New OpenWithProgramSettings With {
                .Id = _id, .Name = _name, .ProgramPath = _programPath, .Arguments = _arguments}
        End Function

    End Class

End Namespace
