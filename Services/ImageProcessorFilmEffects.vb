Imports System
Imports System.Runtime.InteropServices
Imports SkiaSharp

Namespace Services
    Partial Public Class ImageProcessor

        ' Ports of Spoilerhead's ART CTL scripts, V0.02, (C) Spoilerhead 2026.
        ' Original Bibble 5 / AfterShot Pro plugins:
        ' https://github.com/spoilerhead/asp_plugins/tree/master/SiliconBonk
        ' https://github.com/spoilerhead/asp_plugins/tree/master/FatToni
        ' The CTLs consume linear RGB and explicitly encode gamma 2.2. Our insertion point is
        ' encoded sRGB, so decode/encode sRGB at the boundary to avoid applying gamma twice.
        ' Keep the CTL's fast-sine approximation: replacing it with Sin changes the look.
        Private Delegate Sub FilmPixel(ByRef r As Double, ByRef g As Double, ByRef b As Double)

        Private Shared Function FilmClip(v As Double) As Double
            Return Math.Max(0.0, Math.Min(1.0, v))
        End Function

        Private Shared Function FilmSin(x As Double) As Double
            If x < -Math.PI Then
                x += 2.0 * Math.PI
            ElseIf x > Math.PI Then
                x -= 2.0 * Math.PI
            End If
            Return If(x < 0.0, (1.27323954 + 0.405284735 * x) * x,
                                (1.27323954 - 0.405284735 * x) * x)
        End Function

        Private Shared Function FilmDomain(v As Double) As Double
            Return Math.Pow(PointColorMath.SrgbDecode(FilmClip(v)), 1.0 / 2.2)
        End Function

        Private Shared Function FilmOutput(v As Double) As Double
            Return PointColorMath.SrgbEncode(Math.Pow(FilmClip(v), 2.2))
        End Function

        Private Shared Function SiliconBonkPixel(adj As ImageAdjustments) As FilmPixel
            Dim hue = FilmEffectParameters.SiliconBonkHue.ClampValue(adj.SiliconBonkHue) / 360.0
            Dim luma = FilmEffectParameters.SiliconBonkLuma.ClampValue(adj.SiliconBonkLuma) / 100.0
            Dim saturation = FilmEffectParameters.SiliconBonkSaturation.ClampValue(adj.SiliconBonkSaturation) / 100.0
            Dim mids = FilmEffectParameters.SiliconBonkMidpoint.ClampValue(adj.SiliconBonkMidpoint) / 100.0
            Dim contrast = FilmEffectParameters.SiliconBonkContrast.ClampValue(adj.SiliconBonkContrast) / 100.0
            Dim linearContrast = If(contrast > 1.0, 1.0 + 2.0 * (contrast - 1.0) ^ 2, 1.0)
            contrast = Math.Min(contrast, 1.0)
            Dim preserve = adj.SiliconBonkPreserveDetails
            Return Sub(ByRef r As Double, ByRef g As Double, ByRef b As Double)
                       r = FilmDomain(r) : g = FilmDomain(g) : b = FilmDomain(b)
                       Dim lo = Math.Min(r, Math.Min(g, b)), hi = Math.Max(r, Math.Max(g, b))
                       Dim value = 0.299 * r + 0.587 * g + 0.114 * b
                       Dim sat = 0.0, h = 0.0
                       If value > 0.000001 AndAlso hi - lo > 0.000001 Then
                           sat = (hi - lo) / value
                           If hi = r Then
                               h = (g - b) / (hi - lo) / 6.0
                               If h < 0.0 Then h += 1.0
                           ElseIf hi = g Then
                               h = 1.0 / 3.0 + (b - r) / (hi - lo) / 6.0
                           Else
                               h = 2.0 / 3.0 + (r - g) / (hi - lo) / 6.0
                           End If
                       End If
                       Dim alpha = 0.5 * FilmSin((h - hue) * 2.0 * Math.PI + Math.PI / 2.0)
                       alpha *= FilmClip(sat * (2.0 - sat))
                       value *= 1.0 + luma * alpha
                       If preserve Then value = ((-0.4481 * value + 0.5489) * value + 0.7659) * value + 0.03891
                       value += mids * (value - value * value)
                       value = FilmClip(value)
                       value -= contrast / 4.4 * FilmSin(2.0 * Math.PI * value)
                       value = FilmClip((value - 0.5) * linearContrast + 0.5)
                       sat *= saturation
                       If value <= 0.000001 Then
                           r = 0.0 : g = 0.0 : b = 0.0
                       ElseIf sat <= 0.000001 Then
                           r = value : g = value : b = value
                       Else
                           Dim hf = h * 6.0, chroma = value * sat
                           Dim f = hf - Math.Floor(hf)
                           Dim q = chroma * (1.0 - f), t = chroma * f
                           Select Case CInt(Math.Floor(hf))
                               Case 0 : r = chroma : g = t : b = 0.0
                               Case 1 : r = q : g = chroma : b = 0.0
                               Case 2 : r = 0.0 : g = chroma : b = t
                               Case 3 : r = 0.0 : g = q : b = chroma
                               Case 4 : r = t : g = 0.0 : b = chroma
                               Case Else : r = chroma : g = 0.0 : b = q
                           End Select
                           Dim offset = value - (0.299 * r + 0.587 * g + 0.114 * b)
                           r += offset : g += offset : b += offset
                       End If
                       r = FilmOutput(r) : g = FilmOutput(g) : b = FilmOutput(b)
                   End Sub
        End Function

        Private Shared Function FilmHue(p As Double, q As Double, t As Double) As Double
            If t < 0.0 Then t += 1.0
            If t > 1.0 Then t -= 1.0
            If t < 1.0 / 6.0 Then Return p + (q - p) * 6.0 * t
            If t < 0.5 Then Return q
            If t < 2.0 / 3.0 Then Return p + (q - p) * (2.0 / 3.0 - t) * 6.0
            Return p
        End Function

        Private Shared Function FilmHsl(hue As Double, saturation As Double, lightness As Double) As Double()
            Dim h = hue / 360.0, s = saturation / 100.0, l = lightness / 100.0
            If l <= 0.000001 Then Return {0.0, 0.0, 0.0}
            If s <= 0.000001 Then Return {l, l, l}
            Dim q = If(l <= 0.5, l * (1.0 + s), l + s - l * s)
            Dim p = 2.0 * l - q
            Return {FilmHue(p, q, h + 1.0 / 3.0), FilmHue(p, q, h), FilmHue(p, q, h - 1.0 / 3.0)}
        End Function

        Private Shared Function FatToniBlend(tone As Double, value As Double, contrast As Double) As Double
            Dim screen = 1.0 - (1.0 - tone) * (1.0 - value)
            Dim overlay = value * screen + (1.0 - value) * tone * value
            ' Blend the CTL's soft mix and overlay using the shared 0..100 contrast range.
            Return overlay * contrast + FilmClip(value + tone - 0.5) * (1.0 - contrast)
        End Function

        Private Shared Function FatToniPixel(adj As ImageAdjustments) As FilmPixel
            Dim high = FilmHsl(FilmEffectParameters.FatToniHighlightHue.ClampValue(adj.FatToniHighlightHue), FilmEffectParameters.FatToniHighlightSaturation.ClampValue(adj.FatToniHighlightSaturation), 50.0)
            Dim low = FilmHsl(FilmEffectParameters.FatToniShadowHue.ClampValue(adj.FatToniShadowHue), FilmEffectParameters.FatToniShadowSaturation.ClampValue(adj.FatToniShadowSaturation), 50.0)
            Dim basis = FilmHsl(FilmEffectParameters.FatToniBaseHue.ClampValue(adj.FatToniBaseHue), FilmEffectParameters.FatToniBaseSaturation.ClampValue(adj.FatToniBaseSaturation), FilmEffectParameters.FatToniBaseLuminance.ClampValue(adj.FatToniBaseLuminance))
            Dim mids = FilmEffectParameters.FatToniMidpoint.ClampValue(adj.FatToniMidpoint) / 200.0, separation = FilmEffectParameters.FatToniSeparation.ClampValue(adj.FatToniSeparation) / 200.0
            Dim contrast = FilmEffectParameters.FatToniContrast.ClampValue(adj.FatToniContrast) / 100.0
            Return Sub(ByRef r As Double, ByRef g As Double, ByRef b As Double)
                       r = FilmDomain(r) : g = FilmDomain(g) : b = FilmDomain(b)
                       Dim a = FilmClip((r + g + b) / 3.0 + mids)
                       a = FilmClip(a - separation * FilmSin(2.0 * Math.PI * a))
                       r = FilmOutput(basis(0) * FatToniBlend(high(0) * a + low(0) * (1.0 - a), r, contrast))
                       g = FilmOutput(basis(1) * FatToniBlend(high(1) * a + low(1) * (1.0 - a), g, contrast))
                       b = FilmOutput(basis(2) * FatToniBlend(high(2) * a + low(2) * (1.0 - a), b, contrast))
                   End Sub
        End Function

        ''' <summary>One pixel implementation for preview and deep export. Unpremultiply before
        ''' color math so transparent edges do not acquire dark or colored fringes.</summary>
        Private Shared Function ApplyFilmEffect(source As SKBitmap, transform As FilmPixel) As SKBitmap
            If source.ColorType = DeepColorType Then
                Dim result = New SKBitmap(source.Info)
                Try
                    ForEachRow(source.Width, source.Height,
                        Sub(y)
                            Dim row(source.Width * 4 - 1) As Short
                            ReadDeepRow(source, source.GetPixels(), y, row)
                            For x = 0 To source.Width - 1
                                Dim cr, cg, cb As Single, alpha As Integer
                                ReadDeepUnpremultiplied(row, x * 4, source.AlphaType = SKAlphaType.Premul, cr, cg, cb, alpha)
                                If alpha = 0 Then Continue For
                                Dim r = cr / 255.0, g = cg / 255.0, b = cb / 255.0
                                transform(r, g, b)
                                WriteDeepPremultiplied(row, x * 4, source.AlphaType = SKAlphaType.Premul,
                                                      CSng(r * 255.0), CSng(g * 255.0), CSng(b * 255.0), alpha)
                            Next
                            WriteDeepRow(result, result.GetPixels(), y, row)
                        End Sub)
                    Return result
                Catch
                    result.Dispose()
                    Throw
                End Try
            End If
            Dim buffer As Byte() = Nothing, length, stride, ri, gi, bi, ai As Integer
            If Not TryRentRgbaLikeBuffer(source, buffer, length, stride, ri, gi, bi, ai) Then Return CopyBitmap(source)
            Dim output As SKBitmap = Nothing
            Try
                output = New SKBitmap(source.Info)
                Dim premul = source.AlphaType = SKAlphaType.Premul
                ForEachRow(source.Width, source.Height,
                    Sub(y)
                        Dim row(source.Width * 4 - 1) As Byte
                        For x = 0 To source.Width - 1
                            Dim o = y * stride + x * 4, d = x * 4
                            Dim alpha = buffer(o + ai)
                            row(d + ai) = alpha
                            If alpha = 0 Then Continue For
                            Dim scale = If(premul, CDbl(alpha), 255.0)
                            Dim r = buffer(o + ri) / scale, g = buffer(o + gi) / scale, b = buffer(o + bi) / scale
                            transform(r, g, b)
                            Dim dither = DitherMatrix(((y And 7) << 3) Or (x And 7))
                            Dim qr = QuantizeDithered(CSng(r), dither)
                            Dim qg = QuantizeDithered(CSng(g), dither)
                            Dim qb = QuantizeDithered(CSng(b), dither)
                            row(d + ri) = If(premul, PremultiplyByte(qr, alpha), qr)
                            row(d + gi) = If(premul, PremultiplyByte(qg, alpha), qg)
                            row(d + bi) = If(premul, PremultiplyByte(qb, alpha), qb)
                        Next
                        Marshal.Copy(row, 0, IntPtr.Add(output.GetPixels(), y * output.RowBytes), row.Length)
                    End Sub)
                Return output
            Catch
                output?.Dispose()
                Throw
            Finally
                ReturnPooledBuffer(buffer)
            End Try
        End Function

    End Class
End Namespace
