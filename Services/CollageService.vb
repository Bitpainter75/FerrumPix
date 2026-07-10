Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports Avalonia.Media.Imaging
Imports SkiaSharp

Namespace Services

    Public Class CollageOptions
        Public Property OutputPath As String
        Public Property Width As Integer = 2400
        Public Property Columns As Integer = 3
        Public Property Gap As Integer = 24
        Public Property Margin As Integer = 48
        Public Property BackgroundColor As String = "#FFFFFFFF"
        Public Property Format As String = "JPG"
        Public Property Quality As Integer = 90
        Public Property SquareCells As Boolean = True
        ''' "Grid" (Standard-Raster), "Hero" (ein großes Bild + Rest daneben), "Random" (Größe/Rotation zufällig).
        Public Property LayoutMode As String = "Grid"
        ''' Index des groß dargestellten Bilds im Hero-Layout (manuell in der Vorschau wählbar).
        Public Property HeroIndex As Integer = 0
        ''' Seite, auf der das Hero-Bild sitzt: "Left"/"Right"/"Top"/"Bottom" - die übrigen Bilder
        ''' füllen die gegenüberliegende Fläche als Streifen.
        Public Property HeroPosition As String = "Left"
        ''' Seed für den Zufallsmodus, damit "Neu mischen" reproduzierbar per Klick ein neues, aber
        ''' bei gleichbleibenden Optionen stabiles Ergebnis liefert (kein Re-Randomize bei jedem Preview-Tick).
        Public Property RandomSeed As Integer = 0
        ''' Nothing = Bilder behalten ihre ursprüngliche Auswahlreihenfolge (Standard, in allen
        ''' Layouts). Gesetzt (durch "Neu mischen") vertauscht die Reihenfolge, in der Bilder den
        ''' Positionen zugeordnet werden - in ALLEN Layouts, nicht nur im Zufallsmodus. Im
        ''' Hero-Layout bleibt das gewählte Hero-Bild davon unberührt, nur die Reihenfolge der
        ''' übrigen Bilder wird gemischt.
        Public Property OrderSeed As Integer? = Nothing
    End Class

    ''' Position/Größe/Rotation eines einzelnen Bilds in der Collage - vom Layout-Algorithmus
    ''' (BuildGridSlots/BuildHeroSlots/BuildRandomSlots) berechnet, vom Render-Pass nur noch gezeichnet.
    Public Class CollageSlot
        Public Property SourceIndex As Integer
        Public Property X As Single
        Public Property Y As Single
        Public Property Width As Single
        Public Property Height As Single
        Public Property RotationDegrees As Single
    End Class

    Public Class CollageService
        Public Shared Function SaveCollage(imagePaths As IEnumerable(Of String), options As CollageOptions) As Boolean
            If options Is Nothing OrElse String.IsNullOrWhiteSpace(options.OutputPath) Then Return False

            Using surfaceBitmap = RenderCollage(imagePaths, options)
                If surfaceBitmap Is Nothing Then Return False

                Dim format = If(String.Equals(options.Format, "PNG", StringComparison.OrdinalIgnoreCase),
                                SKEncodedImageFormat.Png,
                                If(String.Equals(options.Format, "WEBP", StringComparison.OrdinalIgnoreCase),
                                   SKEncodedImageFormat.Webp,
                                   SKEncodedImageFormat.Jpeg))
                Using image = SKImage.FromBitmap(surfaceBitmap)
                    Using data = image.Encode(format, Math.Max(1, Math.Min(100, options.Quality)))
                        Using fs = File.Open(options.OutputPath, FileMode.Create, FileAccess.Write)
                            data.SaveTo(fs)
                        End Using
                    End Using
                End Using
            End Using

            Return True
        End Function

        ''' Rendert eine verkleinerte Vorschau (Layout, Zuschnitt und Hintergrund identisch zur
        ''' finalen Ausgabe) für die Live-Vorschau im Collage-Dialog, ohne die volle Zielbreite
        ''' zu berechnen. Breite/Abstand/Rand werden proportional auf maxDimension herunterskaliert.
        Public Shared Function RenderPreview(imagePaths As IEnumerable(Of String), options As CollageOptions, maxDimension As Integer) As Bitmap
            If options Is Nothing Then Return Nothing

            Dim scale = If(options.Width > 0, Math.Min(1.0, maxDimension / CDbl(options.Width)), 1.0)
            Dim previewOptions = New CollageOptions With {
                .Width = Math.Max(64, CInt(Math.Round(options.Width * scale))),
                .Columns = options.Columns,
                .Gap = CInt(Math.Round(options.Gap * scale)),
                .Margin = CInt(Math.Round(options.Margin * scale)),
                .BackgroundColor = options.BackgroundColor,
                .LayoutMode = options.LayoutMode,
                .HeroIndex = options.HeroIndex,
                .HeroPosition = options.HeroPosition,
                .RandomSeed = options.RandomSeed,
                .OrderSeed = options.OrderSeed
            }

            Using bitmap = RenderCollage(imagePaths, previewOptions)
                If bitmap Is Nothing Then Return Nothing
                Return ImageProcessor.ToAvaloniaBitmap(bitmap)
            End Using
        End Function

        ''' Zuletzt für die Vorschau berechnete Slots - dem Dialog-Code-Behind erlaubt das, einen
        ''' Klick auf die Vorschau auf den getroffenen Bildindex zurückzurechnen (manuelle Hero-Auswahl).
        Public Shared Property LastPreviewSlots As List(Of CollageSlot) = New List(Of CollageSlot)()

        Private Shared Function RenderCollage(imagePaths As IEnumerable(Of String), options As CollageOptions) As SKBitmap
            If imagePaths Is Nothing OrElse options Is Nothing Then Return Nothing

            Dim paths = imagePaths.
                Where(Function(p) Not String.IsNullOrWhiteSpace(p) AndAlso File.Exists(p)).
                ToList()
            If paths.Count = 0 Then Return Nothing

            Dim width = Math.Max(320, options.Width)
            Dim gap = Math.Max(0, options.Gap)
            Dim margin = Math.Max(0, options.Margin)

            Dim layout = BuildSlots(paths.Count, width, gap, margin, options)
            LastPreviewSlots = layout.Slots
            Dim bg = ParseColor(options.BackgroundColor, SKColors.White)

            Dim surfaceBitmap = New SKBitmap(width, layout.Height, SKColorType.Bgra8888, SKAlphaType.Premul)
            Using canvas = New SKCanvas(surfaceBitmap)
                canvas.Clear(bg)
                ' SKFilterQuality ist abgekündigt; SamplingHigh entspricht der alten Stufe "High" (kubisch/Mitchell).
                Dim samplingHigh = New SKSamplingOptions(SKCubicResampler.Mitchell)
                Using paint = New SKPaint With {.IsAntialias = True}
                    ' "Gap" wird hier als weißer (Hintergrundfarbe) Rahmen um JEDES einzelne Bild
                    ' interpretiert, nicht als Zwischenraum zwischen Zellen - die Zellen selbst liegen
                    ' kantenweise aneinander (siehe BuildGridSlots/BuildHeroSlots), das Bild wird um
                    ' "gap" Pixel nach innen versetzt gezeichnet, sodass die Hintergrundfarbe rundum
                    ' als gleichmäßiger Rahmen durchscheint (auch bei Rotation im Zufallsmodus).
                    Dim border = Math.Min(gap, CInt(Math.Min(width, layout.Height) * 0.2))
                    For Each slot In layout.Slots
                        If slot.SourceIndex < 0 OrElse slot.SourceIndex >= paths.Count Then Continue For
                        Dim path = paths(slot.SourceIndex)
                        Using original = SKBitmap.Decode(path)
                            If original Is Nothing Then Continue For
                            Dim source = ImageOrientationService.ApplyOrientation(original, ImageOrientationService.ReadOrigin(path))
                            Try
                                Dim centerX = slot.X + slot.Width / 2.0F
                                Dim centerY = slot.Y + slot.Height / 2.0F
                                canvas.Save()
                                If slot.RotationDegrees <> 0 Then
                                    canvas.RotateDegrees(slot.RotationDegrees, centerX, centerY)
                                End If
                                Dim innerWidth = Math.Max(1.0F, slot.Width - border * 2)
                                Dim innerHeight = Math.Max(1.0F, slot.Height - border * 2)
                                Dim insetX = slot.X + (slot.Width - innerWidth) / 2.0F
                                Dim insetY = slot.Y + (slot.Height - innerHeight) / 2.0F
                                Dim dst = New SKRect(insetX, insetY, insetX + innerWidth, insetY + innerHeight)
                                Dim src = GetUniformToFillSourceRect(source.Width, source.Height, CInt(innerWidth), CInt(innerHeight))
                                Using image = SKImage.FromBitmap(source)
                                    canvas.DrawImage(image, src, dst, samplingHigh, paint)
                                End Using
                                canvas.Restore()
                            Finally
                                If Not Object.ReferenceEquals(source, original) Then source.Dispose()
                            End Try
                        End Using
                    Next
                End Using
            End Using

            Return surfaceBitmap
        End Function

        Private Shared Function BuildSlots(count As Integer, width As Integer, gap As Integer, margin As Integer, options As CollageOptions) As (Slots As List(Of CollageSlot), Height As Integer)
            Select Case If(options.LayoutMode, "Grid").Trim().ToUpperInvariant()
                Case "HERO"
                    Return BuildHeroSlots(count, width, gap, margin, options)
                Case "RANDOM"
                    Return BuildRandomSlots(count, width, gap, margin, options)
                Case Else
                    Return BuildGridSlots(count, width, gap, margin, options)
            End Select
        End Function

        ''' Fisher-Yates-Shuffle mit festem Seed - liefert bei gleichem Seed/gleicher Anzahl immer
        ''' dieselbe Reihenfolge (reproduzierbar für die Vorschau), bei neuem Seed ("Neu mischen")
        ''' eine andere.
        Private Shared Function ShuffledIndices(count As Integer, seed As Integer) As List(Of Integer)
            Dim indices = Enumerable.Range(0, count).ToList()
            Dim rng = New Random(seed)
            For i = indices.Count - 1 To 1 Step -1
                Dim j = rng.Next(i + 1)
                Dim temp = indices(i)
                indices(i) = indices(j)
                indices(j) = temp
            Next
            Return indices
        End Function

        Private Shared Function BuildGridSlots(count As Integer, width As Integer, gap As Integer, margin As Integer, options As CollageOptions) As (Slots As List(Of CollageSlot), Height As Integer)
            Dim columns = Math.Max(1, Math.Min(12, options.Columns))
            Dim cellWidth = Math.Max(1.0F, CSng(Math.Floor((width - margin * 2) / CDbl(columns))))
            Dim cellHeight = cellWidth
            Dim rows = CInt(Math.Ceiling(count / CDbl(columns)))
            Dim height = margin * 2 + CInt(rows * cellHeight)
            Dim order = If(options.OrderSeed.HasValue, ShuffledIndices(count, options.OrderSeed.Value), Enumerable.Range(0, count).ToList())

            Dim slots As New List(Of CollageSlot)()
            For index = 0 To count - 1
                Dim col = index Mod columns
                Dim row = index \ columns
                slots.Add(New CollageSlot With {
                    .SourceIndex = order(index),
                    .X = margin + col * cellWidth,
                    .Y = margin + row * cellHeight,
                    .Width = cellWidth,
                    .Height = cellHeight,
                    .RotationDegrees = 0
                })
            Next
            Return (slots, height)
        End Function

        ''' Ein Bild (HeroIndex) groß (60% der nutzbaren Fläche, quadratisch) an der über
        ''' HeroPosition gewählten Seite, die übrigen Bilder als Streifen auf der gegenüberliegenden
        ''' Seite, gleichmäßig verteilt.
        Private Shared Function BuildHeroSlots(count As Integer, width As Integer, gap As Integer, margin As Integer, options As CollageOptions) As (Slots As List(Of CollageSlot), Height As Integer)
            If count <= 1 Then Return BuildGridSlots(count, width, gap, margin, options)

            Dim heroIndex = Math.Max(0, Math.Min(count - 1, options.HeroIndex))
            Dim otherIndices = Enumerable.Range(0, count).Where(Function(i) i <> heroIndex).ToList()
            ' Das Hero-Bild selbst bleibt vom "Neu mischen" unberührt (sonst würde die manuell
            ' getroffene Auswahl bei jedem Reshuffle wieder verlorengehen) - nur die Reihenfolge der
            ' übrigen Bilder in ihrem Streifen wird gemischt.
            If options.OrderSeed.HasValue Then
                Dim shuffleOrder = ShuffledIndices(otherIndices.Count, options.OrderSeed.Value)
                otherIndices = shuffleOrder.Select(Function(i) otherIndices(i)).ToList()
            End If
            Dim otherCount = otherIndices.Count
            Dim position = If(options.HeroPosition, "Left").Trim().ToUpperInvariant()
            Dim usableWidth = Math.Max(1, width - margin * 2)
            Dim heroSize = CSng(usableWidth * 0.6)

            Dim slots As New List(Of CollageSlot)()
            Dim height As Integer

            Select Case position
                Case "CENTER"
                    ' Hero mittig, die übrigen Bilder als Rahmen ringsherum verteilt (oben/unten/
                    ' links/rechts), reihum zugewiesen - mosaikartiger "Bild im Rahmen"-Look.
                    Dim usableHeight = usableWidth
                    Dim heroCenterSize = CSng(Math.Min(usableWidth, usableHeight) * 0.5)
                    height = margin * 2 + CInt(usableHeight)
                    Dim sideMargin = Math.Max(1.0F, (usableWidth - heroCenterSize) / 2.0F)
                    Dim topBottomHeight = Math.Max(1.0F, (usableHeight - heroCenterSize) / 2.0F)
                    Dim heroX = margin + sideMargin
                    Dim heroY = margin + topBottomHeight
                    slots.Add(New CollageSlot With {.SourceIndex = heroIndex, .X = heroX, .Y = heroY, .Width = heroCenterSize, .Height = heroCenterSize, .RotationDegrees = 0})

                    If otherCount > 0 Then
                        Dim topOthers = otherIndices.Where(Function(idx, i) i Mod 4 = 0).ToList()
                        Dim bottomOthers = otherIndices.Where(Function(idx, i) i Mod 4 = 1).ToList()
                        Dim leftOthers = otherIndices.Where(Function(idx, i) i Mod 4 = 2).ToList()
                        Dim rightOthers = otherIndices.Where(Function(idx, i) i Mod 4 = 3).ToList()

                        If topOthers.Count > 0 Then
                            Dim w = Math.Max(1.0F, usableWidth / topOthers.Count)
                            For i = 0 To topOthers.Count - 1
                                slots.Add(New CollageSlot With {.SourceIndex = topOthers(i), .X = margin + i * w, .Y = margin, .Width = w, .Height = topBottomHeight, .RotationDegrees = 0})
                            Next
                        End If
                        If bottomOthers.Count > 0 Then
                            Dim w = Math.Max(1.0F, usableWidth / bottomOthers.Count)
                            For i = 0 To bottomOthers.Count - 1
                                slots.Add(New CollageSlot With {.SourceIndex = bottomOthers(i), .X = margin + i * w, .Y = heroY + heroCenterSize, .Width = w, .Height = topBottomHeight, .RotationDegrees = 0})
                            Next
                        End If
                        If leftOthers.Count > 0 Then
                            Dim h = Math.Max(1.0F, heroCenterSize / leftOthers.Count)
                            For i = 0 To leftOthers.Count - 1
                                slots.Add(New CollageSlot With {.SourceIndex = leftOthers(i), .X = margin, .Y = heroY + i * h, .Width = sideMargin, .Height = h, .RotationDegrees = 0})
                            Next
                        End If
                        If rightOthers.Count > 0 Then
                            Dim h = Math.Max(1.0F, heroCenterSize / rightOthers.Count)
                            For i = 0 To rightOthers.Count - 1
                                slots.Add(New CollageSlot With {.SourceIndex = rightOthers(i), .X = heroX + heroCenterSize, .Y = heroY + i * h, .Width = sideMargin, .Height = h, .RotationDegrees = 0})
                            Next
                        End If
                    End If

                Case "TOP", "BOTTOM"
                    Dim heroHeight = heroSize
                    Dim otherHeight = Math.Max(1.0F, usableWidth - heroHeight)
                    height = margin * 2 + CInt(heroHeight + otherHeight)
                    Dim heroY = If(position = "TOP", CSng(margin), margin + otherHeight)
                    Dim otherY = If(position = "TOP", margin + heroHeight, CSng(margin))
                    slots.Add(New CollageSlot With {.SourceIndex = heroIndex, .X = margin, .Y = heroY, .Width = usableWidth, .Height = heroHeight, .RotationDegrees = 0})
                    If otherCount > 0 Then
                        Dim otherWidth = Math.Max(1.0F, usableWidth / otherCount)
                        For i = 0 To otherCount - 1
                            slots.Add(New CollageSlot With {.SourceIndex = otherIndices(i), .X = margin + i * otherWidth, .Y = otherY, .Width = otherWidth, .Height = otherHeight, .RotationDegrees = 0})
                        Next
                    End If

                Case "RIGHT"
                    Dim heroWidth = heroSize
                    Dim otherWidth = Math.Max(1.0F, usableWidth - heroWidth)
                    height = margin * 2 + CInt(heroWidth)
                    slots.Add(New CollageSlot With {.SourceIndex = heroIndex, .X = margin + otherWidth, .Y = margin, .Width = heroWidth, .Height = heroWidth, .RotationDegrees = 0})
                    If otherCount > 0 Then
                        Dim otherHeight = Math.Max(1.0F, heroWidth / otherCount)
                        For i = 0 To otherCount - 1
                            slots.Add(New CollageSlot With {.SourceIndex = otherIndices(i), .X = margin, .Y = margin + i * otherHeight, .Width = otherWidth, .Height = otherHeight, .RotationDegrees = 0})
                        Next
                    End If

                Case Else ' LEFT
                    Dim heroWidth = heroSize
                    Dim otherWidth = Math.Max(1.0F, usableWidth - heroWidth)
                    height = margin * 2 + CInt(heroWidth)
                    slots.Add(New CollageSlot With {.SourceIndex = heroIndex, .X = margin, .Y = margin, .Width = heroWidth, .Height = heroWidth, .RotationDegrees = 0})
                    If otherCount > 0 Then
                        Dim otherHeight = Math.Max(1.0F, heroWidth / otherCount)
                        For i = 0 To otherCount - 1
                            slots.Add(New CollageSlot With {.SourceIndex = otherIndices(i), .X = margin + heroWidth, .Y = margin + i * otherHeight, .Width = otherWidth, .Height = otherHeight, .RotationDegrees = 0})
                        Next
                    End If
            End Select

            Return (slots, height)
        End Function

        ''' Basis bleibt das reguläre Raster - pro Zelle werden Größe (immer etwas GRÖSSER als die
        ''' Zelle, nie kleiner) und Rotation (-12..12°) mit einem festen Seed zufällig verändert,
        ''' damit "Neu mischen" reproduzierbar bleibt, bis der Seed explizit neu gewürfelt wird. Nie
        ''' verkleinern ist wichtig, damit trotz Rotation kein Hintergrund als unerwünschte Lücke
        ''' zwischen den Bildern durchscheint - die Bilder überlappen sich stattdessen leicht.
        Private Shared Function BuildRandomSlots(count As Integer, width As Integer, gap As Integer, margin As Integer, options As CollageOptions) As (Slots As List(Of CollageSlot), Height As Integer)
            Dim grid = BuildGridSlots(count, width, gap, margin, options)
            Dim rng = New Random(options.RandomSeed)

            For Each slot In grid.Slots
                Dim centerX = slot.X + slot.Width / 2.0F
                Dim centerY = slot.Y + slot.Height / 2.0F
                Dim scale = 1.15F + CSng(rng.NextDouble() * 0.2)
                Dim newWidth = slot.Width * scale
                Dim newHeight = slot.Height * scale
                slot.X = centerX - newWidth / 2.0F
                slot.Y = centerY - newHeight / 2.0F
                slot.Width = newWidth
                slot.Height = newHeight
                slot.RotationDegrees = CSng(rng.NextDouble() * 24.0 - 12.0)
            Next

            Return grid
        End Function

        Private Shared Function GetUniformToFillSourceRect(sourceWidth As Integer, sourceHeight As Integer, targetWidth As Integer, targetHeight As Integer) As SKRect
            Dim sourceAspect = sourceWidth / CDbl(sourceHeight)
            Dim targetAspect = targetWidth / CDbl(targetHeight)
            If sourceAspect > targetAspect Then
                Dim cropWidth = CSng(sourceHeight * targetAspect)
                Dim left = (sourceWidth - cropWidth) / 2.0F
                Return New SKRect(left, 0, left + cropWidth, sourceHeight)
            End If

            Dim cropHeight = CSng(sourceWidth / targetAspect)
            Dim top = (sourceHeight - cropHeight) / 2.0F
            Return New SKRect(0, top, sourceWidth, top + cropHeight)
        End Function

        Private Shared Function ParseColor(value As String, fallback As SKColor) As SKColor
            If String.IsNullOrWhiteSpace(value) Then Return fallback
            Dim text = value.Trim().TrimStart("#"c)
            Dim raw As UInteger
            If UInteger.TryParse(text, Globalization.NumberStyles.HexNumber, Globalization.CultureInfo.InvariantCulture, raw) Then
                If text.Length <= 6 Then
                    Return New SKColor(CByte((raw >> 16) And &HFFUI), CByte((raw >> 8) And &HFFUI), CByte(raw And &HFFUI), 255)
                End If
                Return New SKColor(CByte((raw >> 16) And &HFFUI), CByte((raw >> 8) And &HFFUI), CByte(raw And &HFFUI), CByte((raw >> 24) And &HFFUI))
            End If
            Return fallback
        End Function
    End Class

End Namespace
