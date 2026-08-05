Imports System
Imports System.Collections.Generic
Imports System.IO
Imports SkiaSharp

Namespace Services

    ''' <summary>
    ''' Öffnet eine Photoshop-Datei als Ebenenstapel, statt nur ihr fertiges Gesamtbild zu zeigen.
    ''' Das Gegenstück zum Export in <c>ImageProcessor.ExportLayeredPsd</c>.
    '''
    ''' Herauskommt dasselbe Gespann, das auch ein .fpx-Bündel liefert: ein Grundbild als Datei und
    ''' eine Bearbeitung mit Objekt-Ebenen darüber. Der Editor kann damit weiterarbeiten wie mit
    ''' jedem anderen Dokument, ohne dass er von PSD wissen muss.
    '''
    ''' Zwei Entscheidungen stecken darin:
    '''
    ''' Das Grundbild ist NICHT das Gesamtbild der Datei. Läge es unter den Ebenen, wäre jedes
    ''' Bildelement doppelt zu sehen - einmal eingerechnet im Gesamtbild und einmal als Ebene
    ''' darüber. Deckt die unterste Ebene das ganze Dokument, deckend und unverändert gemischt,
    ''' wird SIE das Grundbild; sonst entsteht eine leere Fläche in Dokumentgröße.
    '''
    ''' Was Photoshop nicht als Bildpunkte ablegt - Korrekturebenen, Effekte, Textebenen als solche -
    ''' kommt nicht mit. Es steckt im Gesamtbild der Datei, aber nicht in den Ebenen, und eine leere
    ''' Ebene wäre schlimmer als eine fehlende. Wie viele es waren, steht im Ergebnis.
    ''' </summary>
    Public NotInheritable Class PsdImportService

        Private Sub New()
        End Sub

        Public Class PsdImportResult
            ''' Das entpackte Grundbild als Datei im Temp-Ordner.
            Public Property BaseImagePath As String = ""
            Public Property Adjustments As ImageAdjustments
            ''' Der Temp-Ordner mit Grundbild und Ebenendateien; gehört nach dem Laden dem Editor.
            Public Property TempDir As String = ""
            ''' Wie viele Ebenen die Datei trug, die keine Bildpunkte haben - Korrekturebenen etwa.
            Public Property SkippedLayers As Integer
            Public Property LayerCount As Integer
        End Class

        ''' <summary>Baut aus der Datei ein Dokument mit Ebenen. Nothing, wenn die Datei keine
        ''' Ebenen trägt oder in einer Spielart vorliegt, die der Leser nicht beherrscht - dann
        ''' bleibt der Aufrufer beim gewohnten flachen Gesamtbild.</summary>
        Public Shared Function Import(psdPath As String) As PsdImportResult
            If String.IsNullOrWhiteSpace(psdPath) OrElse Not File.Exists(psdPath) Then Return Nothing

            Dim doc = PsdLayerReader.ReadDocument(psdPath)
            If doc Is Nothing OrElse doc.Layers.Count = 0 Then Return Nothing
            If doc.Width < 1 OrElse doc.Height < 1 Then Return Nothing

            Dim tempDir = Path.Combine(Path.GetTempPath(), "FerrumPix", "psd", Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory(tempDir)
            Dim success = False

            Try
                Dim layers = doc.Layers
                Dim adjustments As New ImageAdjustments()
                Dim baseImagePath As String

                ' Deckt die unterste Ebene das ganze Dokument und wird sie unverändert gemischt, ist
                ' sie das Foto - dann wird sie das Grundbild und nicht noch einmal als Objekt gelegt.
                Dim firstIndex = 0
                If IsUsableAsBackground(layers(0), doc.Width, doc.Height) Then
                    baseImagePath = Path.Combine(tempDir, "base.png")
                    If Not WritePng(layers(0).Pixels, baseImagePath) Then Return Nothing
                    firstIndex = 1
                Else
                    baseImagePath = Path.Combine(tempDir, "base.png")
                    If Not WriteEmptyPng(doc.Width, doc.Height, baseImagePath) Then Return Nothing
                End If

                Dim index = 0
                For i = firstIndex To layers.Count - 1
                    Dim layer = layers(i)
                    If layer.Pixels Is Nothing Then Continue For

                    Dim assetPath = Path.Combine(tempDir, "layer" & index.ToString() & ".png")
                    If Not WritePng(layer.Pixels, assetPath) Then Continue For
                    index += 1

                    adjustments.Annotations.Add(New ImageAnnotation With {
                        .Kind = "Image",
                        .ImagePath = assetPath,
                        .SourceFileName = If(String.IsNullOrWhiteSpace(layer.Name), "Layer", layer.Name),
                        .CustomName = If(layer.Name, ""),
                        .XPixels = layer.Left,
                        .YPixels = layer.Top,
                        .WidthPixels = layer.Width,
                        .HeightPixels = layer.Height,
                        .Opacity = layer.OpacityPercent,
                        .BlendMode = layer.BlendMode,
                        .ClipToLayerBelow = layer.ClipToLayerBelow,
                        .IsVisible = layer.IsVisible,
                        .LockAspect = False
                    })
                Next

                success = True
                Return New PsdImportResult With {
                    .BaseImagePath = baseImagePath,
                    .Adjustments = adjustments,
                    .TempDir = tempDir,
                    .LayerCount = index,
                    .SkippedLayers = CountSkipped(psdPath, layers.Count)
                }
            Catch
                Return Nothing
            Finally
                For Each layer In doc.Layers
                    layer.Pixels?.Dispose()
                Next
                ' Bei erfolgreichem Laden übernimmt der Editor den Temp-Ordner. Bei jedem Fehler wird
                ' er sofort entfernt, sonst bleiben halbe Entpackungen liegen - dieselbe Regel wie
                ' beim .fpx-Bündel.
                If Not success Then
                    Try
                        Directory.Delete(tempDir, True)
                    Catch
                    End Try
                End If
            End Try
        End Function

        ''' <summary>Die unterste Ebene taugt als Grundbild, wenn sie genau auf dem Dokument liegt,
        ''' sichtbar und voll deckend ist und normal gemischt wird. Sonst ginge beim Übernehmen
        ''' etwas verloren, das nur als Ebene richtig wirkt.</summary>
        Private Shared Function IsUsableAsBackground(layer As PsdLayerReader.PsdLayerInfo,
                                                     docWidth As Integer, docHeight As Integer) As Boolean
            If layer Is Nothing OrElse layer.Pixels Is Nothing Then Return False
            If layer.Left <> 0 OrElse layer.Top <> 0 Then Return False
            If layer.Width <> docWidth OrElse layer.Height <> docHeight Then Return False
            If Not layer.IsVisible Then Return False
            If layer.ClipToLayerBelow Then Return False
            If layer.OpacityPercent < 99.5F Then Return False
            Return String.Equals(layer.BlendMode, "Normal", StringComparison.OrdinalIgnoreCase)
        End Function

        ''' <summary>Wie viele Ebenen die Datei trägt, von denen keine Bildpunkte ankamen. Gezählt
        ''' wird über den Namen im Verzeichnis, nicht über das Ergebnis: Korrekturebenen und
        ''' Gruppenmarken fallen im Leser heraus, und der Nutzer soll erfahren, dass etwas fehlt.</summary>
        Private Shared Function CountSkipped(psdPath As String, deliveredLayers As Integer) As Integer
            ' Der Leser liefert nur Ebenen mit Bildpunkten. Die Gesamtzahl steht im Verzeichnis der
            ' Datei; die Differenz sind die uebersprungenen. Schlaegt das Zaehlen fehl, wird lieber
            ' 0 gemeldet als eine erfundene Zahl.
            Try
                Dim total = PsdLayerReader.CountLayerRecords(psdPath)
                If total <= 0 Then Return 0
                Return Math.Max(0, total - deliveredLayers)
            Catch
                Return 0
            End Try
        End Function

        Private Shared Function WritePng(bmp As SKBitmap, targetPath As String) As Boolean
            If bmp Is Nothing Then Return False
            Using pixmap = bmp.PeekPixels()
                If pixmap Is Nothing Then Return False
                Using encoded = pixmap.Encode(SKEncodedImageFormat.Png, 100)
                    If encoded Is Nothing Then Return False
                    Using fs = File.Create(targetPath)
                        encoded.SaveTo(fs)
                    End Using
                End Using
            End Using
            Return True
        End Function

        ''' <summary>Eine leere, durchsichtige Fläche als Grundbild. Sie ist der Ersatz für das
        ''' Gesamtbild: unter den Ebenen darf nichts liegen, was schon in ihnen steckt.</summary>
        Private Shared Function WriteEmptyPng(width As Integer, height As Integer, targetPath As String) As Boolean
            Using bmp = New SKBitmap(New SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul))
                Using canvas = New SKCanvas(bmp)
                    canvas.Clear(SKColors.Transparent)
                End Using
                Return WritePng(bmp, targetPath)
            End Using
        End Function

    End Class

End Namespace
