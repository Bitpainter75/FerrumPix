Imports Avalonia.Logging

Namespace Services

    ''' <summary>Leitet die Meldungen der Oberflaechenschicht in unser Diagnoselog.
    '''
    ''' WOZU: Manche Fragen beantwortet nur die Plattform selbst. Beim Ziehen und Ablegen sagt der
    ''' X11-Teil von Avalonia, worauf er gerade wartet - "Timeout waiting for XdndStatus",
    ''' "Pointer moved ... while waiting for XdndStatus. XdndPosition will be sent later." Ohne diese
    ''' Zeilen sieht man von aussen nur, dass nichts mehr passiert.
    '''
    ''' NUR MIT EINGESCHALTETEM DIAGNOSELOG, und nur die Bereiche, die uns etwas sagen: eine
    ''' ungefilterte Oberflaechenspur schreibt bei jeder Bindung und jedem Layoutlauf und macht die
    ''' Datei unbrauchbar. Ein vorhandener Empfaenger bleibt erhalten und bekommt alles weiterhin.</summary>
    Public NotInheritable Class AvaloniaLogBridge
        Implements ILogSink

        Private Sub New(inner As ILogSink)
            _inner = inner
        End Sub

        Private ReadOnly _inner As ILogSink

        ''' <summary>Welche Bereiche mitgeschrieben werden. X11Platform traegt die Ziehquelle.</summary>
        Private Shared ReadOnly LoggedAreas As String() = {"X11Platform", "DragDrop"}

        Public Shared Sub Install()
            ' Kein zweiter Einbau, wenn schon einer haengt.
            If TypeOf Logger.Sink Is AvaloniaLogBridge Then Return
            Logger.Sink = New AvaloniaLogBridge(Logger.Sink)
        End Sub

        Public Function IsEnabled(level As LogEventLevel, area As String) As Boolean Implements ILogSink.IsEnabled
            If _inner IsNot Nothing AndAlso _inner.IsEnabled(level, area) Then Return True
            Return IsInteresting(area)
        End Function

        Public Sub Log(level As LogEventLevel, area As String, source As Object, messageTemplate As String) Implements ILogSink.Log
            _inner?.Log(level, area, source, messageTemplate)
            If IsInteresting(area) Then Write(level, area, messageTemplate)
        End Sub

        Public Sub Log(level As LogEventLevel, area As String, source As Object, messageTemplate As String, ParamArray propertyValues() As Object) Implements ILogSink.Log
            _inner?.Log(level, area, source, messageTemplate, propertyValues)
            If Not IsInteresting(area) Then Return
            ' Die Platzhalter der Vorlage tragen NAMEN ({Position}, {Window}), keine Nummern - eine
            ' Formatierung ueber String.Format ginge daran vorbei. Die Werte werden deshalb angehaengt.
            Dim values = If(propertyValues Is Nothing OrElse propertyValues.Length = 0, "",
                            " [" & String.Join(", ", propertyValues.Select(Function(w) If(w Is Nothing, "-", w.ToString()))) & "]")
            Write(level, area, messageTemplate & values)
        End Sub

        Private Shared Function IsInteresting(area As String) As Boolean
            If String.IsNullOrEmpty(area) Then Return False
            For Each candidate In LoggedAreas
                If String.Equals(area, candidate, StringComparison.OrdinalIgnoreCase) Then Return True
            Next
            Return False
        End Function

        Private Shared Sub Write(level As LogEventLevel, area As String, text As String)
            DiagnosticLogService.LogAlways("Avalonia." & area, $"{level}: {text}")
        End Sub

    End Class

End Namespace
