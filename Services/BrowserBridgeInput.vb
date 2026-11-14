Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Reflection
Imports System.Text.Json
Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Headless
Imports Avalonia.Input
Imports Avalonia.VisualTree

Namespace Services

    ' Maus und Tastatur des Browsers werden zu Eingaben der Fenster.
    '
    ' DIE EINGABE GEHT UEBER REFLEXION. Die Plattform ohne Bildschirm nimmt Eingaben ueber eine
    ' interne Schnittstelle an (IHeadlessWindow). Die oeffentlichen Erweiterungen darueber sind fuer
    ' Tests gebaut: jede arbeitet vor und nach der Eingabe die ganze Warteschlange ab und erzwingt
    ' mehrere Zeichendurchlaeufe. Fuer ein paar Klicks in einem Test ist das richtig, bei sechzig
    ' Mausbewegungen in der Sekunde nicht. Deshalb der direkte Weg, mit allem, was daran haengt:
    ' die Schnittstelle kann in jeder Fassung des Toolkits anders aussehen.
    Partial Public NotInheritable Class BrowserBridge

        Private Const MouseButtonMask As RawInputModifiers =
            RawInputModifiers.LeftMouseButton Or RawInputModifiers.RightMouseButton Or RawInputModifiers.MiddleMouseButton

        Private Shared _headlessInterface As Type
        Private Shared ReadOnly HeadlessMethods As New Dictionary(Of String, MethodInfo)(StringComparer.Ordinal)

        ''' <summary>Das Fenster, in dem eine Maustaste gedrueckt wurde. Bis sie losgelassen ist,
        ''' gehen alle Mausereignisse dorthin, auch wenn der Zeiger das Fenster verlaesst.</summary>
        Private Shared _pointerOwner As Window
        Private Shared _lastCursor As String

        Private Shared Sub HandleMessage(message As JsonElement)
            Try
                Select Case ReadString(message, "t")
                    Case "resize"
                        ApplyViewport(message)
                    Case "m"
                        HandlePointer(message)
                    Case "k"
                        HandleKey(message)
                End Select
            Catch ex As Exception
                DiagnosticLogService.LogException("BrowserBridge.Input", ex)
            End Try
        End Sub

        Private Shared Sub HandlePointer(message As JsonElement)
            Dim x = ReadNumber(message, "x", 0)
            Dim y = ReadNumber(message, "y", 0)
            Dim modifiers = CType(CInt(ReadNumber(message, "mods", 0)), RawInputModifiers)
            Dim local As Point
            Dim win = PointerTarget(x, y, local)
            If win Is Nothing Then Return

            Select Case ReadString(message, "e")
                Case "move"
                    InvokeHeadless(win, "MouseMove", local, modifiers)
                    UpdateCursor(win, local)
                Case "down"
                    _pointerOwner = win
                    If Not win.IsActive Then win.Activate()
                    InvokeHeadless(win, "MouseDown", local, ToMouseButton(CInt(ReadNumber(message, "b", 0))), modifiers)
                Case "up"
                    InvokeHeadless(win, "MouseUp", local, ToMouseButton(CInt(ReadNumber(message, "b", 0))), modifiers)
                    If (modifiers And MouseButtonMask) = RawInputModifiers.None Then _pointerOwner = Nothing
                Case "wheel"
                    InvokeHeadless(win, "MouseWheel", local,
                                   New Vector(ReadNumber(message, "dx", 0), ReadNumber(message, "dy", 0)), modifiers)
            End Select
        End Sub

        ''' <summary>Welches Fenster ein Mausereignis bekommt, und wo es darin liegt. Die Angaben
        ''' des Browsers sind Punkte der Seite; ein Fenster liegt dort an seiner Lage geteilt durch
        ''' das Pixelverhaeltnis.
        '''
        ''' Ein Dialog sperrt alles darunter. Die Plattform ohne Bildschirm tut das nicht selbst,
        ''' ihr SetEnabled ist leer; ohne diese Regel liesse sich das Hauptfenster hinter einem
        ''' offenen Dialog weiter bedienen.</summary>
        Private Shared Function PointerTarget(x As Double, y As Double, ByRef local As Point) As Window
            Dim windows = VisibleWindows().OrderByDescending(Function(w) StateOf(w).ActivationOrder).ToList()
            If windows.Count = 0 Then Return Nothing

            If _pointerOwner IsNot Nothing AndAlso windows.Contains(_pointerOwner) Then
                local = ToLocal(_pointerOwner, x, y)
                Return _pointerOwner
            End If
            _pointerOwner = Nothing

            For Each win In windows
                local = ToLocal(win, x, y)
                If local.X >= 0 AndAlso local.Y >= 0 AndAlso
                   local.X < win.ClientSize.Width AndAlso local.Y < win.ClientSize.Height Then
                    Return win
                End If
                If win.IsDialog Then Return Nothing
            Next
            Return Nothing
        End Function

        Private Shared Function ToLocal(win As Window, x As Double, y As Double) As Point
            Dim scaling = If(win.RenderScaling > 0, win.RenderScaling, 1.0)
            Return New Point(x - win.Position.X / scaling, y - win.Position.Y / scaling)
        End Function

        Private Shared Sub HandleKey(message As JsonElement)
            Dim win = VisibleWindows().OrderByDescending(Function(w) StateOf(w).ActivationOrder).FirstOrDefault()
            If win Is Nothing Then Return

            Dim modifiers = CType(CInt(ReadNumber(message, "mods", 0)), RawInputModifiers)
            Dim physical = ToPhysicalKey(ReadString(message, "code"))
            Dim symbol = ReadString(message, "key")
            Dim key = ToKey(symbol, physical)
            Dim keySymbol = If(symbol.Length = 1, symbol, Nothing)

            If ReadString(message, "e") = "up" Then
                InvokeHeadless(win, "KeyRelease", key, modifiers, physical, keySymbol)
                Return
            End If

            InvokeHeadless(win, "KeyPress", key, modifiers, physical, keySymbol)
            Dim text = ReadString(message, "text")
            If text.Length > 0 Then InvokeHeadless(win, "TextInput", text)
        End Sub

        ''' <summary>Der Tastenort aus dem Browser (<c>KeyboardEvent.code</c>). Das Toolkit benennt
        ''' seine Tastenorte nach derselben Norm, bis auf zwei Schreibweisen: die Buchstaben ohne
        ''' das vorangestellte "Key" und der Ziffernblock als "NumPad".</summary>
        Friend Shared Function ToPhysicalKey(code As String) As PhysicalKey
            If String.IsNullOrEmpty(code) OrElse Not Char.IsLetter(code(0)) Then Return PhysicalKey.None
            Dim name = code
            If name.Length = 4 AndAlso name.StartsWith("Key", StringComparison.Ordinal) Then
                name = name.Substring(3)
            ElseIf name.StartsWith("Numpad", StringComparison.Ordinal) Then
                name = "NumPad" & name.Substring(6)
            End If
            Dim result As PhysicalKey
            If [Enum].TryParse(name, False, result) AndAlso [Enum].IsDefined(result) Then Return result
            Return PhysicalKey.None
        End Function

        ''' <summary>Die Taste nach ihrer BEDEUTUNG. Buchstaben und Ziffern kommen aus dem Zeichen,
        ''' das der Browser meldet, nicht aus dem Tastenort: auf einer deutschen Tastatur liegt Z
        ''' dort, wo die Norm Y fuehrt, und ein Kuerzel mit Z soll die Taste meinen, auf der Z
        ''' steht. Alles Uebrige folgt dem Tastenort.</summary>
        Friend Shared Function ToKey(symbol As String, physical As PhysicalKey) As Key
            If symbol IsNot Nothing AndAlso symbol.Length = 1 Then
                Dim character = Char.ToUpperInvariant(symbol(0))
                If character >= "A"c AndAlso character <= "Z"c Then
                    Return CType(CInt(Key.A) + (AscW(character) - AscW("A"c)), Key)
                End If
                ' Die Ziffern des Ziffernblocks behalten ihren eigenen Tastenwert.
                If character >= "0"c AndAlso character <= "9"c AndAlso
                   Not physical.ToString().StartsWith("NumPad", StringComparison.Ordinal) Then
                    Return CType(CInt(Key.D0) + (AscW(character) - AscW("0"c)), Key)
                End If
                Select Case character
                    Case "+"c
                        If physical <> PhysicalKey.NumPadAdd Then Return Key.OemPlus
                    Case "-"c
                        If physical <> PhysicalKey.NumPadSubtract Then Return Key.OemMinus
                    Case ","c
                        If physical <> PhysicalKey.NumPadComma AndAlso physical <> PhysicalKey.NumPadDecimal Then Return Key.OemComma
                    Case "."c
                        If physical <> PhysicalKey.NumPadDecimal Then Return Key.OemPeriod
                End Select
            End If
            Return physical.ToQwertyKey()
        End Function

        ''' <summary>Maustaste des Browsers (0 links, 1 Mitte, 2 rechts, 3 und 4 die Seitentasten).</summary>
        Friend Shared Function ToMouseButton(button As Integer) As MouseButton
            Select Case button
                Case 0
                    Return MouseButton.Left
                Case 1
                    Return MouseButton.Middle
                Case 2
                    Return MouseButton.Right
                Case 3
                    Return MouseButton.XButton1
                Case 4
                    Return MouseButton.XButton2
                Case Else
                    Return MouseButton.None
            End Select
        End Function

        ''' <summary>Der Mauszeiger. Die Plattform ohne Bildschirm verwirft ihn, deshalb wird er
        ''' hier nachgebildet: das Element unter der Maus und seine Vorfahren werden nach einem
        ''' gesetzten Zeiger gefragt, und der Browser bekommt den Namen. Selbst gezeichnete Zeiger
        ''' (ein Pinselkreis etwa) lassen sich so nicht uebertragen und erscheinen als Pfeil.</summary>
        Private Shared Sub UpdateCursor(win As Window, local As Point)
            Dim name = "Arrow"
            Dim element = TryCast(win.InputHitTest(local), Visual)
            While element IsNot Nothing
                Dim input = TryCast(element, InputElement)
                If input IsNot Nothing AndAlso input.Cursor IsNot Nothing Then
                    name = input.Cursor.ToString()
                    Exit While
                End If
                element = element.GetVisualParent()
            End While
            If String.Equals(name, _lastCursor, StringComparison.Ordinal) Then Return
            _lastCursor = name
            Dim ignored = SendTextAsync("{""t"":""cursor"",""name"":""" & JsonEncodedText.Encode(name).ToString() & """}")
        End Sub

        ''' <summary>Ruft eine Methode der internen Eingabeschnittstelle des Fensters auf. Gibt es
        ''' die Schnittstelle oder die Methode nicht (mehr), passiert nichts, und der Aufruf meldet
        ''' das zurueck.</summary>
        Friend Shared Function InvokeHeadless(win As Window, name As String, ParamArray arguments As Object()) As Boolean
            Dim impl = win?.PlatformImpl
            If impl Is Nothing Then Return False
            If _headlessInterface Is Nothing Then
                _headlessInterface = GetType(AvaloniaHeadlessPlatform).Assembly.GetType("Avalonia.Headless.IHeadlessWindow")
            End If
            If _headlessInterface Is Nothing OrElse Not _headlessInterface.IsInstanceOfType(impl) Then Return False

            Dim method As MethodInfo = Nothing
            If Not HeadlessMethods.TryGetValue(name, method) Then
                method = _headlessInterface.GetMethod(name)
                HeadlessMethods(name) = method
            End If
            If method Is Nothing Then Return False
            Try
                method.Invoke(impl, arguments)
                Return True
            Catch ex As TargetInvocationException
                DiagnosticLogService.LogException("BrowserBridge.Input", If(ex.InnerException, ex))
                Return False
            End Try
        End Function
    End Class
End Namespace
