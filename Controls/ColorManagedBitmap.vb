Imports System
Imports System.Globalization
Imports Avalonia
Imports Avalonia.Data.Converters
Imports Avalonia.Media
Imports Avalonia.Media.Imaging
Imports Avalonia.Platform
Imports Avalonia.Rendering.SceneGraph
Imports Avalonia.Skia
Imports FerrumPix.Services
Imports SkiaSharp

Namespace Controls

    ''' <summary>Ein Foto als Bildquelle, das beim Zeichnen auf das Profil des Bildschirms
    ''' umgerechnet wird (<see cref="DisplayProfileService"/>).
    '''
    ''' <para>WARUM EINE HÜLLE UND KEIN EIGENES BILD-ELEMENT: <c>Image.Render</c> ist in Avalonia 12
    ''' versiegelt. Eine Bildquelle (<see cref="IImage"/>) zeichnet sich dagegen selbst; hier über eine
    ''' eigene Zeichenoperation, die auf der Skia-Zeichenfläche eine Ebene mit dem Farbfilter öffnet,
    ''' das Foto mit Avalonias eigenem Weg hineinzeichnet und die Ebene schließt. Das Foto wird dabei
    ''' nicht kopiert: der Editor beschreibt seine Bitmap fortlaufend neu, eine Kopie wäre bei jedem
    ''' Bild veraltet.</para>
    '''
    ''' <para>Die Pixel bleiben unberührt. Pipette, Histogramm und Speichern lesen weiter sRGB.</para></summary>
    Public NotInheritable Class ColorManagedBitmap
        Implements IImage

        Public ReadOnly Property Bitmap As Bitmap

        Public Sub New(bitmap As Bitmap)
            Me.Bitmap = bitmap
        End Sub

        Public ReadOnly Property Size As Size Implements IImage.Size
            Get
                Return Bitmap.Size
            End Get
        End Property

        Public Sub Draw(context As DrawingContext, sourceRect As Rect, destRect As Rect) Implements IImage.Draw
            Dim filter = DisplayProfileService.CurrentFilter
            If filter Is Nothing Then
                context.DrawImage(Bitmap, sourceRect, destRect)
                Return
            End If
            context.Custom(New FilteredBitmapOperation(Bitmap, sourceRect, destRect, filter))
        End Sub

        Private Shared ReadOnly _wrappers As New System.Runtime.CompilerServices.ConditionalWeakTable(Of Bitmap, ColorManagedBitmap)()

        ''' <summary>Hüllt ein Foto für eine Bindung ein. Nothing bleibt Nothing, alles andere als
        ''' eine Bitmap geht unverändert durch.
        '''
        ''' DIESELBE BITMAP BEKOMMT DIESELBE HÜLLE. Der Editor meldet seine Bitmap nach jedem
        ''' Neubeschreiben erneut, und der Code-behind setzt die Zoom-Details bei jedem Layout. Eine
        ''' neue Hülle wäre für Avalonia jedes Mal eine neue Quelle, also Messen und Anordnen bei jedem
        ''' Bild. Die Zuordnung ist schwach und hält die Bitmap nicht am Leben.</summary>
        Public Shared Function Wrap(source As Object) As Object
            Dim bitmap = TryCast(source, Bitmap)
            If bitmap Is Nothing Then Return source
            Return _wrappers.GetValue(bitmap, Function(b) New ColorManagedBitmap(b))
        End Function

        ''' <summary>Öffnet eine Ebene mit dem Farbfilter, zeichnet das Foto auf Avalonias Weg
        ''' hinein (Interpolation und Deckkraft wie sonst auch) und schließt sie wieder. Die Leihe der
        ''' Zeichenfläche setzt beim Zurückgeben nur die Transformation zurück; die Ebene bleibt über
        ''' beide Leihen offen und wird von der zweiten geschlossen.</summary>
        Private NotInheritable Class FilteredBitmapOperation
            Implements ICustomDrawOperation

            Private ReadOnly _bitmap As Bitmap
            Private ReadOnly _sourceRect As Rect
            Private ReadOnly _destRect As Rect
            Private ReadOnly _filter As SKColorFilter

            Public Sub New(bitmap As Bitmap, sourceRect As Rect, destRect As Rect, filter As SKColorFilter)
                _bitmap = bitmap
                _sourceRect = sourceRect
                _destRect = destRect
                _filter = filter
            End Sub

            Public ReadOnly Property Bounds As Rect Implements ICustomDrawOperation.Bounds
                Get
                    Return _destRect
                End Get
            End Property

            Public Function HitTest(p As Point) As Boolean Implements ICustomDrawOperation.HitTest
                Return _destRect.Contains(p)
            End Function

            ''' <summary>NIE GLEICH. Der Editor beschreibt seine Bitmap an Ort und Stelle neu und
            ''' zeichnet dann neu an; dieselbe Bitmap mit denselben Rechtecken hiesse für den
            ''' Compositor "unverändert", und er liesse das alte Bild stehen. Neu aufgezeichnet wird
            ''' ohnehin nur, wenn das Bild-Element es verlangt.</summary>
            Public Overloads Function Equals(other As ICustomDrawOperation) As Boolean Implements IEquatable(Of ICustomDrawOperation).Equals
                Return False
            End Function

            Public Sub Render(context As ImmediateDrawingContext) Implements ICustomDrawOperation.Render
                Dim feature = TryCast(context.TryGetFeature(GetType(ISkiaSharpApiLeaseFeature)), ISkiaSharpApiLeaseFeature)
                If feature Is Nothing Then
                    context.DrawBitmap(_bitmap, _sourceRect, _destRect)
                    Return
                End If
                Dim canvas As SKCanvas
                Dim saved As Integer
                Using lease = feature.Lease()
                    canvas = lease.SkCanvas
                    Using paint = New SKPaint With {.ColorFilter = _filter}
                        saved = canvas.SaveLayer(New SKRect(CSng(_destRect.Left), CSng(_destRect.Top),
                                                            CSng(_destRect.Right), CSng(_destRect.Bottom)), paint)
                    End Using
                End Using
                Try
                    context.DrawBitmap(_bitmap, _sourceRect, _destRect)
                Finally
                    Using lease = feature.Lease()
                        lease.SkCanvas.RestoreToCount(saved)
                    End Using
                End Try
            End Sub

            Public Sub Dispose() Implements IDisposable.Dispose
            End Sub
        End Class
    End Class

    ''' <summary>Für XAML: <c>Source="{Binding DisplayImage, Converter={x:Static
    ''' controls:ColorManagedBitmapConverter.Instance}}"</c>.</summary>
    Public NotInheritable Class ColorManagedBitmapConverter
        Implements IValueConverter

        Public Shared ReadOnly Instance As New ColorManagedBitmapConverter()

        Public Function Convert(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.Convert
            Return ColorManagedBitmap.Wrap(value)
        End Function

        Public Function ConvertBack(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.ConvertBack
            Dim managed = TryCast(value, ColorManagedBitmap)
            Return If(managed IsNot Nothing, managed.Bitmap, value)
        End Function
    End Class

End Namespace
