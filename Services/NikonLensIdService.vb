Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.Linq
Imports MetadataExtractor
Imports MetadataExtractor.Formats.Exif
Imports MetadataExtractor.Formats.Exif.Makernotes

Namespace Services

    ''' <summary>Loest Nikons verschluesselte Lens-ID auf. Diese kleine Tabelle ist nur fuer die
    ''' sonst nicht unterscheidbaren Fremdobjektive da.
    '''
    ''' <para>REIHENFOLGE, und die ist Absicht: Eine getroffene ID schlaegt das EXIF-LensModel,
    ''' weil neuere Gehaeuse dort fuer ein Fremdobjektiv den Namen eines eigenen eintragen, und
    ''' erst recht Nikons eigenen Lens-Tag. Der traegt bei Fremdobjektiven nur Brennweite und
    ''' Blende ("18-35mm f/1.8") und nennt den Hersteller gar nicht - ihm den Vorrang zu lassen
    ''' hiesse, die Aufloesung wegzuwerfen, fuer die es diese Klasse gibt. Eine unbekannte ID
    ''' faellt auf LensModel und dann auf den Nikon-Tag zurueck.</para></summary>
    Public NotInheritable Class NikonLensIdService

        Private Sub New()
        End Sub

        ' Die beiden 256-Byte-Substitutionstabellen sind Teil des von Nikon verwendeten
        ' MakerNote-Verfahrens. Die Entschluesselung selbst ist symmetrisch und braucht daneben
        ' nur Seriennummer und Ausloesezaehler aus derselben Datei.
        Private Shared ReadOnly _serialTable As Byte() = Convert.FromHexString(
            "c1bf6d0d59c5139d83616b4fc77f3d3d5359e3c7e92f95a7951fdf7f2b29c70ddf07ef71893d133d3b13fb0d89c1651fb30d6b29e3fbefa36b477f9535a7474fc7f1599535112961f13db32b0d4389c19d9d8965f1e9dfbf3d7f5397e5e995171d3d8bfbc7e367a707f171a753b52989e52ba71729e94fc5656d6bef0d89492fb34353651d49a3138959ef6bef651d0b5913e34f9db329432b071d95595947fbe5e961472f357f177fef7f959571d3a30b71a3ad0b3bb5fba3bf4f831dade92f7165a3e507353d0db5e9e5473b9def35a3bfb3df53d397534971073561712f432f11df1797fb953b7f6bd325bfadc7c5c5b58bef2fd3076b25499525496d71c7")
        Private Shared ReadOnly _countTable As Byte() = Convert.FromHexString(
            "a7bcc9ad91df85e5d478d517467c294c4d03e925681186b3bdf76f6122a226342abe1e4614689d4418c240f47e5f1bad0b94b667b40be1ea959c66dce75d6c05dad5df7aeff6db1f824cc06847a1bdee3950564adddfa5f8c6daca90ca01429d8b0c7343750594de24b38034e52cdc9b3fca3345d0db5ff552c321dae222726b3ed05ba8878c065d0fdd091993d0b9fc8b0f8460331c9b45f1f0a3943a1277334d4478283c9efd655716946bfb59d0c82236dbd2639843a1048786f7a626bbd6594dbf6a2eaa2befe678b64ee02fdc7cbe5719327e2ad0b8ba29003c527da8493b2deb2549faa3aa39a7c5a7501136fbc6674af5a512657eb0dfaf4eb3617f2f")

        ' Nur eindeutig bestaetigte IDs aufnehmen. Eine unbekannte ID bleibt bei der lesbaren
        ' Nikon-Angabe, statt anhand Brennweite und Blende einen falschen Hersteller zu erfinden.
        ' Jede ID ist an einer Datei des Bestands belegt; der Name ist, wo es eines gibt, der des
        ' passenden Profils mit Nikon-F-Anschluss in den Objektivdaten.
        Private Shared ReadOnly _knownLenses As New Dictionary(Of String, String)(StringComparer.Ordinal) From {
            {"A14119312C2C4B06", "Sigma 10-20mm f/3.5 EX DC HSM"},
            {"8F482B5024244B0E", "Sigma 17-50mm f/2.8 EX DC OS HSM"},
            {"8E3F2B5C24304B0E", "Sigma 17-70mm f/2.8-4 DC MACRO OS HSM Contemporary"},
            {"8E482B5C24304B0E", "Sigma 17-70mm f/2.8-4 DC MACRO OS HSM Contemporary"},
            {"26402D702B3C1C06", "Sigma 18-125mm f/3.5-5.6 DC"},
            {"92352D882C404B0E", "Sigma 18-250mm f/3.5-6.3 DC OS Macro HSM"},
            {"8B4C2D4414144B06", "Sigma 18-35mm f/1.8 DC HSM Art"},
            {"7F482D5024241C06", "Sigma 18-50mm f/2.8 EX DC Macro"},
            {"26402D502C3C1C06", "Sigma 18-50mm f/3.5-5.6 DC"},
            {"915444440C0C4B06", "Sigma 35mm f/1.4 DG HSM | A"},
            {"885450500C0C4B06", "Sigma 50mm f/1.4 DG HSM [A]"},
            {"CE3476A038404B0E", "Sigma 150-500mm f/5-6.3 APO DG OS HSM"},
            {"823476A638404B0E", "Sigma 150-600mm f/5-6.3 DG OS HSM | C"},
            {"E6402D802C40DF0E", "Tamron 18-200mm f/3.5-6.3 Di II VC"},
            {"00402D802C400006", "Tamron AF 18-200mm f/3.5-6.3 XR Di II LD Aspherical (IF) Macro"},
            {"00402D882C406206", "Tamron AF 18-250mm f/3.5-6.3 Di II LD Aspherical (IF) Macro"},
            {"FE48375C2424DF0E", "Tamron SP 24-70mm f/2.8 Di VC USD"},
            {"FA543C5E2424DF06", "Tamron SP AF 28-75mm f/2.8 XR Di LD Aspherical (IF) Macro"},
            {"E84C44441414DF0E", "Tamron SP 35mm f/1.8 Di VC USD F012"},
            {"FE545C802424DF0E", "Tamron SP 70-200mm f/2.8 Di VC USD A009"},
            {"F1475C8E303CDF0E", "Tamron SP 70-300mm f/4-5.6 Di VC USD (A005)"},
            {"0040182B2C340006", "Tokina AT-X 107 AF DX Fisheye 10-17mm f/3.5-4.5"}
        }

        Public Shared Function TryGetLensName(metaDirectories As IEnumerable(Of Directory)) As String
            Dim id = TryGetLensId(metaDirectories)
            Dim result As String = Nothing
            Return If(_knownLenses.TryGetValue(id, result), result, "")
        End Function

        ''' <summary>Die entschluesselte acht Byte lange Nikon-ID als Hexwert. Oeffentlich nur
        ''' fuer den Diagnosepruefstand; die Anwendung verwendet <see cref="TryGetLensName"/>.</summary>
        Public Shared Function TryGetLensId(metaDirectories As IEnumerable(Of Directory)) As String
            If metaDirectories Is Nothing Then Return ""
            Dim nikon = metaDirectories.OfType(Of NikonType2MakernoteDirectory)().FirstOrDefault()
            If nikon Is Nothing Then Return ""
            Dim raw = nikon.GetByteArray(NikonType2MakernoteDirectory.TagLensData)
            If raw Is Nothing OrElse raw.Length < 20 Then Return ""

            ' Wo die Kennbytes stehen und ob der Block verschluesselt ist, haengt an der Fassung in
            ' den ersten vier Bytes. Am ganzen Nikon-Bestand nachgemessen: 0100 klar ab 6, 0101 klar
            ' ab 11, 0201 bis 0203 verschluesselt ab 11, 0204 ab 12, 0800 bis 0802 ab 13. Eine fest
            ' angenommene 12 traf nur 0204 und lieferte bei 0201 bis 0203 eine um ein Byte
            ' verschobene ID, die nie in der Tabelle stand.
            Dim version = System.Text.Encoding.ASCII.GetString(raw, 0, 4)
            Dim offset As Integer
            Dim encrypted As Boolean
            Select Case version
                Case "0100" : offset = 6 : encrypted = False
                Case "0101" : offset = 11 : encrypted = False
                Case "0201", "0202", "0203" : offset = 11 : encrypted = True
                Case "0204" : offset = 12 : encrypted = True
                Case "0800", "0801", "0802" : offset = 13 : encrypted = True
                Case Else : Return ""
            End Select

            Dim decoded = DirectCast(raw.Clone(), Byte())
            If encrypted Then
                ' Aeltere Kameras schreiben keine Seriennummer als Zahl; fuer sie nimmt Nikons
                ' Verfahren einen festen Wert (0x22 bei der D50, sonst 0x60).
                Dim serialText = nikon.GetDescription(NikonType2MakernoteDirectory.TagCameraSerialNumber)
                Dim serial As Long
                If Not Long.TryParse(If(serialText, "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, serial) Then
                    Dim model = metaDirectories.OfType(Of ExifIfd0Directory)().FirstOrDefault()?.GetDescription(ExifDirectoryBase.TagModel)
                    serial = If(If(model, "").Trim().EndsWith("D50", StringComparison.Ordinal), &H22L, &H60L)
                End If
                Dim count As Long
                Try
                    count = nikon.GetInt64(NikonType2MakernoteDirectory.TagExposureSequenceNumber)
                Catch
                    Return ""
                End Try

                Dim countKey As Integer = CInt((count Xor (count >> 8) Xor (count >> 16) Xor (count >> 24)) And &HFFL)
                Dim ci As Integer = _serialTable(CInt(serial And &HFFL))
                Dim cj As Integer = _countTable(countKey)
                Dim ck As Integer = &H60
                For i = 4 To decoded.Length - 1
                    cj = (cj + ci * ck) And &HFF
                    ck = (ck + 1) And &HFF
                    decoded(i) = CByte(decoded(i) Xor cj)
                Next
            End If

            ' Lauter Nullen heisst: kein Objektiv mit Kennung (ohne CPU oder ein Z-Objektiv, das
            ' seinen Namen anders meldet).
            If decoded.Skip(offset).Take(7).All(Function(b) b = 0) Then Return ""

            ' Die ersten sieben Kennbytes stehen im verschluesselten LensData-Block. LensType
            ' (D/G/VR-Bits) ist dagegen ein eigener, unverschluesselter Nikon-Tag und bildet
            ' das achte Byte der handelsueblichen Lens-ID.
            Dim lensType As Integer
            Try
                lensType = nikon.GetInt32(NikonType2MakernoteDirectory.TagLensType)
            Catch
                Return ""
            End Try
            Dim idBytes(7) As Byte
            Array.Copy(decoded, offset, idBytes, 0, 7)
            idBytes(7) = CByte(lensType And &HFF)
            Return Convert.ToHexString(idBytes)
        End Function
    End Class
End Namespace
