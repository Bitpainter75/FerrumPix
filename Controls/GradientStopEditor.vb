Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Data
Imports Avalonia.Input
Imports Avalonia.Media
Imports Avalonia.Rendering
Imports FerrumPix.Services

Namespace Controls

    ''' <summary>Die Verlaufsleiste der Gruppe Füllung: oben der Verlauf ueber einem Schachbrett
    ''' (damit Deckkraft sichtbar wird), darunter je Farbstopp ein Anfasser.
    '''
    ''' Bedienung wie in den ueblichen Programmen: ein Klick auf eine freie Stelle setzt einen Stopp
    ''' mit der Farbe, die dort gerade liegt; Ziehen verschiebt ihn; nach unten aus der Leiste ziehen,
    ''' Rechtsklick oder ENTF entfernt ihn (zwei bleiben immer); Pfeiltasten schieben den markierten
    ''' um einen Prozentpunkt, mit SHIFT um zehn.
    '''
    ''' Gelesen und geschrieben wird die Stoppliste als Zeichenkette (Stops, Form von
    ''' GradientFillSpec). Farbe und Lage des MARKIERTEN Stopps stehen in SelectedColor und
    ''' SelectedPosition, an die das Panel Farbwaehler und Zahlenfeld bindet.
    '''
    ''' Die Liste ist hier eine Liste von Objekten und nicht von Werten: beim Ziehen ueber einen
    ''' anderen Stopp hinweg aendert sich die Reihenfolge, der markierte Stopp muss dabei derselbe
    ''' bleiben. Ueber einen Index ginge er verloren.</summary>
    Public Class GradientStopEditor
        Inherits Control
        Implements ICustomHitTest

        Public Shared ReadOnly StopsProperty As StyledProperty(Of String) =
            AvaloniaProperty.Register(Of GradientStopEditor, String)(NameOf(Stops), "#FF000000@0;#FFFFFFFF@100", defaultBindingMode:=BindingMode.TwoWay)
        Public Shared ReadOnly SelectedColorProperty As StyledProperty(Of Color) =
            AvaloniaProperty.Register(Of GradientStopEditor, Color)(NameOf(SelectedColor), Colors.Black, defaultBindingMode:=BindingMode.TwoWay)
        Public Shared ReadOnly SelectedPositionProperty As StyledProperty(Of Double) =
            AvaloniaProperty.Register(Of GradientStopEditor, Double)(NameOf(SelectedPosition), 0.0, defaultBindingMode:=BindingMode.TwoWay)
        Public Shared ReadOnly CanRemoveStopProperty As StyledProperty(Of Boolean) =
            AvaloniaProperty.Register(Of GradientStopEditor, Boolean)(NameOf(CanRemoveStop), False)

        Private Const BarTop As Double = 1
        Private Const BarHeight As Double = 22
        Private Const HandleTop As Double = BarTop + BarHeight + 2
        Private Const HandleHeight As Double = 14
        Private Const HandleHalfWidth As Double = 6
        ''' <summary>So weit unter die Leiste gezogen, wird der Stopp beim Loslassen entfernt.</summary>
        Private Const RemoveDistance As Double = 28

        Private NotInheritable Class EditorStop
            Public Color As Color
            Public Position As Double
        End Class

        Private ReadOnly _stops As New List(Of EditorStop)()
        Private _selected As EditorStop
        Private _dragging As EditorStop
        Private _dragRemoving As Boolean
        Private _syncing As Boolean
        Private _lastWritten As String

        Shared Sub New()
            AffectsRender(Of GradientStopEditor)(StopsProperty)
            FocusableProperty.OverrideDefaultValue(Of GradientStopEditor)(True)
        End Sub

        Public Sub New()
            MinHeight = HandleTop + HandleHeight + 2
            Cursor = New Cursor(StandardCursorType.Hand)
            ReadStops()
        End Sub

        Public Property Stops As String
            Get
                Return GetValue(StopsProperty)
            End Get
            Set(value As String)
                SetValue(StopsProperty, value)
            End Set
        End Property

        Public Property SelectedColor As Color
            Get
                Return GetValue(SelectedColorProperty)
            End Get
            Set(value As Color)
                SetValue(SelectedColorProperty, value)
            End Set
        End Property

        Public Property SelectedPosition As Double
            Get
                Return GetValue(SelectedPositionProperty)
            End Get
            Set(value As Double)
                SetValue(SelectedPositionProperty, value)
            End Set
        End Property

        ''' <summary>Ob sich der markierte Stopp entfernen laesst (mehr als zwei Stopps).</summary>
        Public Property CanRemoveStop As Boolean
            Get
                Return GetValue(CanRemoveStopProperty)
            End Get
            Set(value As Boolean)
                SetValue(CanRemoveStopProperty, value)
            End Set
        End Property

        Public Function HitTest(point As Point) As Boolean Implements ICustomHitTest.HitTest
            Return New Rect(Bounds.Size).Contains(point)
        End Function

        ''' <summary>Entfernt den markierten Stopp, solange danach noch zwei bleiben.</summary>
        Public Sub RemoveSelectedStop()
            If _selected Is Nothing OrElse _stops.Count <= 2 Then Return
            Dim index = _stops.IndexOf(_selected)
            _stops.Remove(_selected)
            _selected = _stops(Math.Max(0, Math.Min(_stops.Count - 1, index - 1)))
            WriteStops()
        End Sub

        Protected Overrides Sub OnPropertyChanged(change As AvaloniaPropertyChangedEventArgs)
            MyBase.OnPropertyChanged(change)
            If _syncing Then Return
            If change.Property Is StopsProperty Then
                ' Die eigene Schreibung kommt ueber die Bindung zurueck: nicht neu einlesen, sonst
                ' verlöre der markierte Stopp mitten im Ziehen seine Identitaet.
                If Not String.Equals(Stops, _lastWritten, StringComparison.Ordinal) Then ReadStops()
            ElseIf change.Property Is SelectedColorProperty Then
                If _selected Is Nothing OrElse _selected.Color = SelectedColor Then Return
                _selected.Color = SelectedColor
                WriteStops()
            ElseIf change.Property Is SelectedPositionProperty Then
                If _selected Is Nothing Then Return
                Dim position = Math.Max(0.0, Math.Min(100.0, SelectedPosition))
                If Math.Abs(position - _selected.Position) < 0.05 Then Return
                _selected.Position = position
                WriteStops()
            End If
        End Sub

        ''' <summary>Liest die Stoppliste neu ein und behaelt die Markierung an derselben Stelle der
        ''' Reihenfolge.</summary>
        Private Sub ReadStops()
            Dim previousIndex = If(_selected Is Nothing, 0, _stops.IndexOf(_selected))
            _stops.Clear()
            For Each s In GradientFillSpec.ParseStops(Stops, "#FF000000", "#FFFFFFFF")
                _stops.Add(New EditorStop With {.Color = Color.FromArgb(s.A, s.R, s.G, s.B), .Position = s.Position})
            Next
            _selected = _stops(Math.Max(0, Math.Min(_stops.Count - 1, previousIndex)))
            _dragging = Nothing
            PublishSelection()
            InvalidateVisual()
        End Sub

        Private Sub WriteStops()
            SortStops()
            Dim text = GradientFillSpec.FormatStops(_stops.Where(Function(s) Not (s Is _dragging AndAlso _dragRemoving)).
                Select(Function(s) New GradientStopValue((CUInt(s.Color.A) << 24) Or (CUInt(s.Color.R) << 16) Or (CUInt(s.Color.G) << 8) Or CUInt(s.Color.B), s.Position)))
            _lastWritten = text
            _syncing = True
            Try
                Stops = text
            Finally
                _syncing = False
            End Try
            PublishSelection()
            InvalidateVisual()
        End Sub

        Private Sub SortStops()
            ' Stabil: zwei Stopps an derselben Stelle behalten ihre Reihenfolge.
            Dim ordered = _stops.Select(Function(s, i) (s, i)).OrderBy(Function(x) x.s.Position).ThenBy(Function(x) x.i).Select(Function(x) x.s).ToList()
            _stops.Clear()
            _stops.AddRange(ordered)
        End Sub

        Private Sub PublishSelection()
            _syncing = True
            Try
                If _selected IsNot Nothing Then
                    SelectedColor = _selected.Color
                    SelectedPosition = Math.Round(_selected.Position, 1)
                End If
                CanRemoveStop = _stops.Count > 2
            Finally
                _syncing = False
            End Try
        End Sub

        Private Function BarLeft() As Double
            Return HandleHalfWidth
        End Function

        Private Function BarWidth() As Double
            Return Math.Max(1.0, Bounds.Width - 2 * HandleHalfWidth)
        End Function

        Private Function XFor(position As Double) As Double
            Return BarLeft() + BarWidth() * position / 100.0
        End Function

        Private Function PositionFor(x As Double) As Double
            Return Math.Max(0.0, Math.Min(100.0, (x - BarLeft()) / BarWidth() * 100.0))
        End Function

        Private Function FindHandle(point As Point) As EditorStop
            ' Der zuletzt gezeichnete (markierte) liegt oben und gewinnt bei Ueberlappung.
            Dim best As EditorStop = Nothing
            Dim bestDistance = HandleHalfWidth + 2
            For Each s In _stops
                Dim d = Math.Abs(point.X - XFor(s.Position))
                If d < bestDistance OrElse (s Is _selected AndAlso d <= bestDistance) Then
                    best = s
                    bestDistance = d
                End If
            Next
            Return best
        End Function

        Protected Overrides Sub OnPointerPressed(e As PointerPressedEventArgs)
            MyBase.OnPointerPressed(e)
            Focus()
            Dim point = e.GetPosition(Me)
            Dim properties = e.GetCurrentPoint(Me).Properties
            Dim hit = FindHandle(point)

            If properties.IsRightButtonPressed Then
                If hit IsNot Nothing Then
                    _selected = hit
                    RemoveSelectedStop()
                End If
                e.Handled = True
                Return
            End If
            If Not properties.IsLeftButtonPressed Then Return

            If hit Is Nothing Then
                ' Neuer Stopp mit der Farbe, die dort schon liegt: der Verlauf aendert sich durch das
                ' Setzen nicht, erst durch das Umfaerben oder Verschieben.
                Dim position = PositionFor(point.X)
                Dim current = _stops.Select(Function(s) New GradientStopValue((CUInt(s.Color.A) << 24) Or (CUInt(s.Color.R) << 16) Or (CUInt(s.Color.G) << 8) Or CUInt(s.Color.B), s.Position)).ToList()
                Dim argb = GradientFillSpec.ColorAt(current, position)
                hit = New EditorStop With {
                    .Color = Color.FromArgb(CByte((argb >> 24) And &HFFUI), CByte((argb >> 16) And &HFFUI), CByte((argb >> 8) And &HFFUI), CByte(argb And &HFFUI)),
                    .Position = position}
                _stops.Add(hit)
                _selected = hit
                WriteStops()
            Else
                _selected = hit
                PublishSelection()
                InvalidateVisual()
            End If
            _dragging = hit
            _dragRemoving = False
            e.Pointer.Capture(Me)
            e.Handled = True
        End Sub

        Protected Overrides Sub OnPointerMoved(e As PointerEventArgs)
            MyBase.OnPointerMoved(e)
            If _dragging Is Nothing Then Return
            Dim point = e.GetPosition(Me)
            Dim removing = _stops.Count > 2 AndAlso point.Y > HandleTop + HandleHeight + RemoveDistance
            Dim position = PositionFor(point.X)
            If removing = _dragRemoving AndAlso Math.Abs(position - _dragging.Position) < 0.05 Then Return
            _dragRemoving = removing
            If Not removing Then _dragging.Position = position
            WriteStops()
        End Sub

        Protected Overrides Sub OnPointerReleased(e As PointerReleasedEventArgs)
            MyBase.OnPointerReleased(e)
            If _dragging Is Nothing Then Return
            Dim removed = _dragRemoving
            Dim stopToRemove = _dragging
            _dragging = Nothing
            _dragRemoving = False
            e.Pointer.Capture(Nothing)
            If removed Then
                _selected = stopToRemove
                RemoveSelectedStop()
            End If
        End Sub

        Protected Overrides Sub OnPointerCaptureLost(e As PointerCaptureLostEventArgs)
            MyBase.OnPointerCaptureLost(e)
            If _dragging Is Nothing Then Return
            Dim wasRemoving = _dragRemoving
            _dragging = Nothing
            _dragRemoving = False
            ' Abgebrochen: der Stopp bleibt, wo er zuletzt stand.
            If wasRemoving Then WriteStops()
        End Sub

        Protected Overrides Sub OnKeyDown(e As KeyEventArgs)
            MyBase.OnKeyDown(e)
            If _selected Is Nothing Then Return
            Select Case e.Key
                Case Key.Delete, Key.Back
                    RemoveSelectedStop()
                    e.Handled = True
                Case Key.Left, Key.Right
                    Dim stepSize = If(e.KeyModifiers.HasFlag(KeyModifiers.Shift), 10.0, 1.0)
                    _selected.Position = Math.Max(0.0, Math.Min(100.0, _selected.Position + If(e.Key = Key.Left, -stepSize, stepSize)))
                    WriteStops()
                    e.Handled = True
            End Select
        End Sub

        Public Overrides Sub Render(context As DrawingContext)
            MyBase.Render(context)
            Dim bar = New Rect(BarLeft(), BarTop, BarWidth(), BarHeight)
            DrawChecker(context, bar)

            Dim visible = _stops.Where(Function(s) Not (s Is _dragging AndAlso _dragRemoving)).ToList()
            Dim brush As New LinearGradientBrush With {
                .StartPoint = New RelativePoint(0, 0.5, RelativeUnit.Relative),
                .EndPoint = New RelativePoint(1, 0.5, RelativeUnit.Relative)}
            For Each s In visible
                brush.GradientStops.Add(New GradientStop(s.Color, s.Position / 100.0))
            Next
            Dim border = FindBrush("FP.Border.Strong", Brushes.Gray)
            context.DrawRectangle(brush, New Pen(border, 1), bar, 3, 3)

            Dim accent = FindBrush("FP.Accent", Brushes.Orange)
            Dim handlePen = New Pen(FindBrush("FP.Text.Secondary", Brushes.LightGray), 1)
            ' Der markierte zuletzt, damit er bei Ueberlappung oben liegt.
            For Each s In visible.Where(Function(v) v IsNot _selected).Concat(visible.Where(Function(v) v Is _selected))
                Dim x = XFor(s.Position)
                Dim isSelected = s Is _selected
                ' Fuenfeck mit Spitze nach oben, wie ein Zeiger auf die Stelle im Verlauf.
                Dim geometry = New StreamGeometry()
                Using ctx = geometry.Open()
                    ctx.BeginFigure(New Point(x, HandleTop), True)
                    ctx.LineTo(New Point(x + HandleHalfWidth, HandleTop + 5))
                    ctx.LineTo(New Point(x + HandleHalfWidth, HandleTop + HandleHeight))
                    ctx.LineTo(New Point(x - HandleHalfWidth, HandleTop + HandleHeight))
                    ctx.LineTo(New Point(x - HandleHalfWidth, HandleTop + 5))
                    ctx.EndFigure(True)
                End Using
                ' Unter die Farbe ein Schachbrett-Ersatz: halb Weiss, halb Grau, damit Durchsichtiges
                ' als solches erkennbar bleibt.
                context.DrawGeometry(Brushes.White, Nothing, geometry)
                context.DrawRectangle(New SolidColorBrush(Color.FromRgb(160, 160, 160)), Nothing,
                                      New Rect(x, HandleTop + 5, HandleHalfWidth, (HandleHeight - 5) / 2))
                context.DrawRectangle(New SolidColorBrush(Color.FromRgb(160, 160, 160)), Nothing,
                                      New Rect(x - HandleHalfWidth, HandleTop + 5 + (HandleHeight - 5) / 2, HandleHalfWidth, (HandleHeight - 5) / 2))
                context.DrawGeometry(New SolidColorBrush(s.Color), If(isSelected, New Pen(accent, 2), handlePen), geometry)
            Next
        End Sub

        Private Shared Sub DrawChecker(context As DrawingContext, area As Rect)
            Const cell As Double = 5
            context.DrawRectangle(Brushes.White, Nothing, area, 3, 3)
            Dim grey = New SolidColorBrush(Color.FromRgb(200, 200, 200))
            Using context.PushClip(area)
                Dim row = 0
                Dim y = area.Top
                While y < area.Bottom
                    Dim x = area.Left + If(row Mod 2 = 0, 0.0, cell)
                    While x < area.Right
                        context.DrawRectangle(grey, Nothing, New Rect(x, y, cell, cell))
                        x += 2 * cell
                    End While
                    y += cell
                    row += 1
                End While
            End Using
        End Sub

        Private Function FindBrush(key As String, fallback As IBrush) As IBrush
            Dim value As Object = Nothing
            If Me.TryFindResource(key, value) AndAlso TypeOf value Is IBrush Then Return DirectCast(value, IBrush)
            Return fallback
        End Function
    End Class

End Namespace
