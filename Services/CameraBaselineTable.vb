Imports System
Imports System.Collections.Generic
Imports System.Text

Namespace Services

    ''' <summary>
    ''' Referenzwerte der Grundbelichtung je Kameramodell.
    '''
    ''' WOZU: Unsere Basisstufe traegt eine feste Grundbelichtung (RawDecodeService.GrundbelichtungEv).
    ''' Adobe hinterlegt den entsprechenden Wert JE KAMERA - unserer ist an genau einer Kamera
    ''' gefittet. Auf anderen Modellen entwickelt er systematisch zu hell, gemessen bis knapp eine
    ''' Blendenstufe. Diese Tabelle gleicht die Modelle UNTEREINANDER an.
    '''
    ''' WIE GEMESSEN: je Modell der Tonwert-Versatz unserer Basisstufe zur kamerainternen
    ''' JPEG-Vorschau (Median der Luminanz, Vorschau nach ihrem Orientierungs-Tag gedreht), ueber
    ''' 235 Aufnahmen aus 217 Modellen und 14 Marken (5 Modelle verworfen, deren Vorschau
    ''' unbrauchbar war - jenseits von 1,2 EV ist das keine Grundbelichtung mehr, sondern ein Messfehler). Der Versatz ist weit ueberwiegend eine
    ''' Kameraeigenschaft: Streuung innerhalb eines Modells 0,07 EV, zwischen den Modellen 0,44 EV.
    '''
    ''' WAS DIE TABELLE NICHT KANN: Die kamerainterne Vorschau traegt den BILDSTIL des Herstellers.
    ''' Die Streuung innerhalb einer Marke ist klein, die Mediane zwischen den Marken unterscheiden
    ''' sich aber deutlich - ein Teil des Versatzes ist also Stil, nicht Sensoreigenschaft. Getrennt
    ''' werden koennte das nur mit Referenzexporten mehrerer Kameras. Deshalb ist die Tabelle eine
    ''' EINSTELLUNG und nicht das Standardverhalten.
    '''
    ''' VERANKERUNG: Alle Werte sind RELATIV zu verstehen. Angewendet wird die Differenz zur
    ''' Referenzkamera, an der die Grundbelichtung gefittet wurde - die behaelt damit exakt ihr
    ''' bisheriges Ergebnis. Die Referenzkamera ist selbst untypisch (1,5 Streuungen unter dem
    ''' Median); sobald ein zweiter echter Referenzexport vorliegt, gehoert ReferenzVersatzEv
    ''' nachgezogen und die Verankerung geprueft.
    ''' </summary>
    Public NotInheritable Class CameraBaselineTable

        Private Sub New()
        End Sub

        ''' <summary>Versatz der Kamera, an der GrundbelichtungEv gefittet wurde (Canon EOS R6).
        ''' Nur diese eine Zahl verankert die Tabelle absolut - alles andere ist relativ.</summary>
        Private Const ReferenceOffsetEv As Double = -0.26

        ''' <summary>Aeusserste Grenze der Verschiebung. Ein einzelner Tabellenwert kann durch eine
        ''' unbrauchbare Vorschau danebenliegen; ohne Deckel wuerde daraus ein unbrauchbares Bild.</summary>
        Private Const MaxShiftEv As Double = 1.0

        ' Modellschluessel (Marke+Modell, nur Buchstaben und Ziffern, gross) = gemessener Versatz in EV.
        Private Const RawData As String =
            "CANONEOS10D=+0.656;CANONEOS1DMARKII=+0.632;CANONEOS1DMARKIII=+0.044;CANONEOS1DMARKIIN=+0.251;" &
            "CANONEOS1DSMARKII=+0.247;CANONEOS1DX=+0.322;CANONEOS20D=+0.213;CANONEOS300DDIGITAL=+0.554;" &
            "CANONEOS30D=+0.138;CANONEOS350DDIGITAL=+0.285;CANONEOS400DDIGITAL=+0.399;CANONEOS50D=+0.575;" &
            "CANONEOS550D=+0.152;CANONEOS5D=+0.191;CANONEOS5DMARKIII=+0.141;CANONEOS5DS=-0.085;" &
            "CANONEOS60D=+0.278;CANONEOS650D=+0.195;CANONEOS6D=+0.097;CANONEOS70D=-0.037;CANONEOS760D=+0.015;" &
            "CANONEOS7D=+0.325;CANONEOSD30=+0.437;CANONEOSD60=+0.432;CANONEOSKISSDIGITAL=+0.685;" &
            "CANONEOSM3=+0.034;CANONEOSREBELT3=+0.018;CANONEOSREBELT6I=+0.094;CANONPOWERSHOTG1=+0.864;" &
            "CANONPOWERSHOTG11=+0.619;CANONPOWERSHOTG15=+0.580;CANONPOWERSHOTG1XMARKII=+0.572;" &
            "CANONPOWERSHOTG2=+0.462;CANONPOWERSHOTG3=+0.502;CANONPOWERSHOTG6=+0.546;" &
            "CANONPOWERSHOTG7X=+0.316;CANONPOWERSHOTPRO1=+0.606;CANONPOWERSHOTS30=+0.368;" &
            "CANONPOWERSHOTS40=+0.537;CANONPOWERSHOTS45=+0.583;CANONPOWERSHOTS50=+0.576;" &
            "CANONPOWERSHOTS60=+0.466;CANONPOWERSHOTS70=+0.428;CANONPOWERSHOTSX60HS=+0.448;" &
            "EASTMANKODAKEASYSHAREZ1015ISDIGITALCAMERA=+0.666;EASTMANKODAKP880ZOOMDIGITALCAMERA=+0.625;" &
            "FUJIFILMFINEPIXE550=+0.546;FUJIFILMFINEPIXE900=+0.654;FUJIFILMFINEPIXF600EXR=+0.580;" &
            "FUJIFILMFINEPIXF700=+0.507;FUJIFILMFINEPIXHS10HS11=+0.867;FUJIFILMFINEPIXHS20EXR=+0.476;" &
            "FUJIFILMFINEPIXS200EXR=+0.448;FUJIFILMFINEPIXS2PRO=+0.339;FUJIFILMFINEPIXS3PRO=+0.669;" &
            "FUJIFILMFINEPIXS5000=+0.532;FUJIFILMFINEPIXS5600=+0.875;FUJIFILMFINEPIXS5PRO=+0.402;" &
            "FUJIFILMFINEPIXS6500FD=+0.523;FUJIFILMFINEPIXS9500=+0.684;FUJIFILMFINEPIXS9600=+0.667;" &
            "FUJIFILMFINEPIXX100=+0.212;FUJIFILMX10=+0.542;FUJIFILMX100S=+0.079;FUJIFILMX100T=+0.067;" &
            "FUJIFILMX20=-0.276;FUJIFILMX30=+0.413;FUJIFILMXA2=+0.098;FUJIFILMXE1=+0.164;FUJIFILMXE2=-0.906;" &
            "FUJIFILMXPRO1=-0.360;FUJIFILMXQ1=+0.374;FUJIFILMXQ2=-0.363;FUJIFILMXT1=+0.204;" &
            "FUJIFILMXT10=+0.331;KONICADYNAX5D=+0.897;KONICADYNAX7D=+0.921;MINOLTADIMAGE7=+0.639;" &
            "MINOLTADIMAGE7HI=+0.500;MINOLTADIMAGE7I=+0.591;MINOLTADIMAGEA1=+0.458;NIKON1S2=+0.132;" &
            "NIKON1V1=+0.042;NIKOND100=+0.601;NIKOND1X=+0.498;NIKOND200=+0.712;NIKOND2X=+1.026;" &
            "NIKOND3=+0.109;NIKOND300=+0.207;NIKOND3100=+0.099;NIKOND3200=-0.067;NIKOND3300=+0.183;" &
            "NIKOND3X=+0.052;NIKOND40=+0.617;NIKOND4S=+0.023;NIKOND50=+0.496;NIKOND5000=+0.117;" &
            "NIKOND5100=-0.262;NIKOND5200=+0.055;NIKOND5300=+0.050;NIKOND5500=-0.348;NIKOND60=+0.188;" &
            "NIKOND600=+0.151;NIKOND610=-0.288;NIKOND70=+0.582;NIKOND700=+0.122;NIKOND7000=+0.136;" &
            "NIKOND70S=+0.560;NIKOND7100=+0.628;NIKOND7200=-0.412;NIKOND750=-0.078;NIKOND80=+0.579;" &
            "NIKOND800=-0.105;NIKOND90=+0.453;NIKONDF=-0.127;NIKONE5400=+0.407;NIKONE5700=+0.805;" &
            "OLYMPUSE3=+0.585;OLYMPUSE30=+0.141;OLYMPUSE410=+0.643;OLYMPUSE420=+0.472;OLYMPUSE450=+0.570;" &
            "OLYMPUSE5=+0.908;OLYMPUSE510=+0.650;OLYMPUSE520=+0.265;OLYMPUSE600=+0.546;OLYMPUSEM1=+0.065;" &
            "OLYMPUSEM10=-0.019;OLYMPUSEM10MARKII=-0.049;OLYMPUSEM5=+0.059;OLYMPUSEM5MARKII=-0.047;" &
            "OLYMPUSEP1=+0.022;OLYMPUSEP2=+0.410;OLYMPUSEP3=+0.256;OLYMPUSEPL1=+0.114;OLYMPUSEPL3=+0.434;" &
            "OLYMPUSEPL5=-0.019;OLYMPUSEPL6=+0.116;OLYMPUSEPL7=+0.006;OLYMPUSEPM1=+0.425;OLYMPUSTG4=+0.428;" &
            "OLYMPUSXZ1=+0.442;OLYMPUSXZ2=+0.391;ONEPLUSONEA0001=+0.512;PANASONICDMCFZ1000=+0.313;" &
            "PANASONICDMCFZ150=+0.518;PANASONICDMCFZ18=+0.714;PANASONICDMCFZ28=+0.998;" &
            "PANASONICDMCFZ38=+0.296;PANASONICDMCFZ70=+0.468;PANASONICDMCFZ72=+0.160;PANASONICDMCG1=+0.667;" &
            "PANASONICDMCG3=+0.320;PANASONICDMCGF1=+0.685;PANASONICDMCGH2=+0.485;PANASONICDMCGH4=-0.126;" &
            "PANASONICDMCGX1=+0.309;PANASONICDMCL10=+0.736;PANASONICDMCLF1=+0.263;PANASONICDMCLX3=+0.454;" &
            "PANASONICDMCLX5=+0.443;PANASONICDMCLX7=+0.558;PANASONICDMCTZ60=+0.906;PANASONICDMCTZ70=+0.505;" &
            "PENTAXISTD=+0.734;PENTAXISTDL=+0.720;PENTAXISTDL2=+0.634;PENTAXISTDS=+0.461;PENTAXK100D=+0.303;" &
            "PENTAXK100DSUPER=+0.538;PENTAXK10D=+0.450;PENTAXK200D=+0.614;PENTAXK20D=+0.441;PENTAXK30=+0.484;" &
            "PENTAXK5=+0.035;PENTAXK50=+0.590;PENTAXK5IIS=+0.350;PENTAXK7=+0.474;PENTAXKM=+0.521;" &
            "PENTAXKR=+0.410;PENTAXKX=+0.604;RICOHGR=+0.921;RICOHPENTAXK3=+0.375;RICOHPENTAXK3II=+0.425;" &
            "RICOHPENTAXKS1=+0.634;SAMSUNGGX20=+0.379;SONYDSCF828=+0.598;SONYDSCR1=+0.458;SONYDSCRX10=+0.271;" &
            "SONYDSCRX100=+0.983;SONYDSCRX100M2=+0.104;SONYDSCRX100M3=+0.454;SONYDSCRX100M4=+0.404;" &
            "SONYDSCRX10M2=+0.475;SONYDSLRA100=+0.442;SONYDSLRA200=+0.111;SONYDSLRA300=-0.046;" &
            "SONYDSLRA330=+0.414;SONYDSLRA350=+0.159;SONYDSLRA550=+0.232;SONYDSLRA580=+0.100;" &
            "SONYDSLRA700=+0.171;SONYDSLRA850=+0.099;SONYDSLRA900=+0.278;SONYILCA77M2=+0.391;" &
            "SONYILCE6000=+0.195;SONYILCE7M2=+0.082;SONYILCE7RM2=+0.028;SONYNEX3=+0.530;SONYNEX3N=+0.090;" &
            "SONYNEX5R=+0.115;SONYNEX6=-0.046;SONYNEX7=+0.127;SONYSLTA35=-0.010;SONYSLTA55V=+0.481;" &
            "SONYSLTA58=+0.232;SONYSLTA77V=+0.184;"

        Private Shared ReadOnly Table As Dictionary(Of String, Double) = BuildTable()

        Private Shared Function BuildTable() As Dictionary(Of String, Double)
            Dim d As New Dictionary(Of String, Double)(StringComparer.Ordinal)
            For Each entry In RawData.Split(";"c)
                If entry.Length = 0 Then Continue For
                Dim p = entry.IndexOf("="c)
                If p <= 0 Then Continue For
                Dim value As Double
                If Double.TryParse(entry.Substring(p + 1), Globalization.NumberStyles.Float,
                                   Globalization.CultureInfo.InvariantCulture, value) Then
                    d(entry.Substring(0, p)) = value
                End If
            Next
            Return d
        End Function

        ''' <summary>Schluessel aus Hersteller und Modell: nur Buchstaben und Ziffern, gross.
        ''' Die Marke wird vorangestellt, wenn das Modell sie nicht schon enthaelt - Nikon schreibt
        ''' "NIKON D800", Fujifilm nur "X-Pro1".</summary>
        Friend Shared Function Key(maker As String, modell As String) As String
            Dim token = LettersAndDigitsOnly(If(maker, "").Split(" "c)(0))
            Dim m = LettersAndDigitsOnly(modell)
            If m.Length = 0 Then Return ""
            Return If(token.Length > 0 AndAlso m.StartsWith(token, StringComparison.Ordinal), m, token & m)
        End Function

        Private Shared Function LettersAndDigitsOnly(s As String) As String
            If String.IsNullOrEmpty(s) Then Return ""
            Dim sb As New StringBuilder(s.Length)
            For Each c In s.ToUpperInvariant()
                If (c >= "A"c AndAlso c <= "Z"c) OrElse (c >= "0"c AndAlso c <= "9"c) Then sb.Append(c)
            Next
            Return sb.ToString()
        End Function

        ''' <summary>Anzahl der hinterlegten Modelle - fuer die Diagnose und die Einstellungsseite.</summary>
        Public Shared ReadOnly Property ModelCount As Integer
            Get
                Return Table.Count
            End Get
        End Property

        ''' <summary>Die Grundbelichtung fuer diese Kamera. Unbekanntes Modell oder fehlende Angaben:
        ''' der uebergebene Standardwert bleibt unveraendert.</summary>
        Public Shared Function BaseExposureFor(maker As String, modell As String,
                                                   standardEv As Double) As Double
            Dim k = Key(maker, modell)
            If k.Length = 0 Then Return standardEv
            Dim offset As Double
            If Not Table.TryGetValue(k, offset) Then Return standardEv
            ' Relativ zur Referenzkamera: die behaelt exakt ihren bisherigen Wert.
            Dim delta = offset - ReferenceOffsetEv
            If delta > MaxShiftEv Then delta = MaxShiftEv
            If delta < -MaxShiftEv Then delta = -MaxShiftEv
            Return standardEv - delta
        End Function

    End Class

End Namespace
