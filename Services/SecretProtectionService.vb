Imports System.Security.Cryptography
Imports System.Text

Namespace Services

    ''' <summary>Verschluesselt die Zugangsdaten in der Einstellungsdatei.
    '''
    ''' WAS DAS IST UND WAS NICHT: Der Schluessel entsteht aus einem Geheimnis, das IM PROGRAMM
    ''' liegt. Wer die Anwendung hat, hat also auch den Schluessel - das ist keine Sicherheit gegen
    ''' jemanden, der den Rechner in der Hand hat, und soll auch keine sein. Es ist eine HUERDE:
    ''' der Immich-Schluessel und das Nextcloud-App-Passwort stehen nicht mehr im Klartext in einer
    ''' Datei, die beim Sichern, beim Bildschirmfoto oder im Anhang einer Fehlermeldung schnell
    ''' woanders landet. Wer sie trotzdem lesen will, muss sich das Geheimnis aus dem Programm
    ''' holen; das ist ein Schritt mehr, und mehr wird hier nicht behauptet.
    '''
    ''' Der echte Schutz waere der Tresor des Systems (Secret Service, Keychain, Credential
    ''' Manager). Er ist plattform- und unter Flatpak portalabhaengig und ein eigenes Vorhaben; die
    ''' Entscheidung dazu steht in Audits/FALLEN_UND_ENTSCHEIDUNGEN.md.
    '''
    ''' KEINE GERAETEBINDUNG, und das ist Absicht. Ein Schluessel, der zusaetzlich an die Maschine
    ''' gebunden ist, macht ein gesichertes Profil auf einem neuen Rechner unbrauchbar - und wer
    ''' seine Einstellungen mitnimmt, wuerde die Zugangsdaten verlieren, ohne zu verstehen warum.
    ''' Der Zweck ist "nicht im Klartext lesbar", nicht "nur hier lesbar".</summary>
    Public NotInheritable Class SecretProtectionService

        Private Sub New()
        End Sub

        ''' <summary>Erkennungszeichen am Anfang eines verschluesselten Wertes. Es traegt die Fassung
        ''' mit: ein Wechsel des Verfahrens bekommt eine neue Zahl, und alte Werte bleiben lesbar.</summary>
        Public Const Prefix As String = "fpx1:"

        ' Das Geheimnis, aus dem der Schluessel entsteht. In Teilen geschrieben, damit es nicht als
        ' eine zusammenhaengende Zeichenkette in der Programmdatei steht - dieselbe Ueberlegung wie
        ' beim Verfahren selbst: eine Huerde, keine Zusicherung.
        Private Shared ReadOnly PepperParts As String() = {"FerrumPix", "settings", "v1", "3f7a9c2e"}
        Private Shared ReadOnly SaltParts As String() = {"fpx", "salt", "2026", "b41d"}
        Private Const Iterations As Integer = 120000
        Private Const NonceBytes As Integer = 12
        Private Const TagBytes As Integer = 16

        Private Shared _key As Byte() = Nothing
        Private Shared ReadOnly _keyLock As New Object()

        ''' <summary>Der abgeleitete Schluessel, EINMAL je Programmlauf. PBKDF2 mit sechsstelliger
        ''' Rundenzahl kostet einen Augenblick; die Einstellungen werden aber an dutzenden Stellen
        ''' gelesen, und je Abfrage neu abzuleiten waere spuerbar.</summary>
        Private Shared Function GetKey() As Byte()
            SyncLock _keyLock
                If _key IsNot Nothing Then Return _key
                Dim pepper = Encoding.UTF8.GetBytes(String.Join("-", PepperParts))
                Dim salt = Encoding.UTF8.GetBytes(String.Join("-", SaltParts))
                _key = Rfc2898DeriveBytes.Pbkdf2(pepper, salt, Iterations, HashAlgorithmName.SHA256, 32)
                Return _key
            End SyncLock
        End Function

        Public Shared Function IsProtected(value As String) As Boolean
            Return Not String.IsNullOrEmpty(value) AndAlso value.StartsWith(Prefix, StringComparison.Ordinal)
        End Function

        ''' <summary>Verschluesselt einen Wert. Ein leerer bleibt leer - eine Chiffre fuer "nichts"
        ''' waere nur Rauschen in der Datei -, und ein bereits verschluesselter wird nicht doppelt
        ''' verpackt.</summary>
        Public Shared Function Protect(plainText As String) As String
            If String.IsNullOrEmpty(plainText) Then Return If(plainText, "")
            If IsProtected(plainText) Then Return plainText
            Try
                Dim klar = Encoding.UTF8.GetBytes(plainText)
                Dim nonce = RandomNumberGenerator.GetBytes(NonceBytes)
                Dim geheim(klar.Length - 1) As Byte
                Dim marke(TagBytes - 1) As Byte
                Using aes = New AesGcm(GetKey(), TagBytes)
                    aes.Encrypt(nonce, klar, geheim, marke)
                End Using
                ' Reihenfolge im Blob: Nonce, Marke, Chiffre. Die ersten beiden haben feste Laengen,
                ' das Zerlegen braucht deshalb keine Laengenangabe.
                Dim blob(NonceBytes + TagBytes + geheim.Length - 1) As Byte
                Buffer.BlockCopy(nonce, 0, blob, 0, NonceBytes)
                Buffer.BlockCopy(marke, 0, blob, NonceBytes, TagBytes)
                Buffer.BlockCopy(geheim, 0, blob, NonceBytes + TagBytes, geheim.Length)
                Return Prefix & Convert.ToBase64String(blob)
            Catch ex As Exception
                ' Lieber im Klartext gespeichert als gar nicht: die Zugangsdaten sind der Zweck der
                ' Einstellung, die Verschleierung ist die Zugabe.
                DiagnosticLogService.LogException("SecretProtection.Protect", ex)
                Return plainText
            End Try
        End Function

        ''' <summary>Entschluesselt einen Wert. WAS NICHT VERSCHLUESSELT IST, KOMMT UNVERAENDERT
        ''' ZURUECK - so liest die Anwendung eine aeltere Einstellungsdatei weiter, und beim naechsten
        ''' Speichern ist der Wert von selbst verschluesselt. Eine eigene Umstellung braucht es
        ''' dafuer nicht.</summary>
        Public Shared Function Unprotect(value As String) As String
            If Not IsProtected(value) Then Return If(value, "")
            Try
                Dim blob = Convert.FromBase64String(value.Substring(Prefix.Length))
                If blob.Length <= NonceBytes + TagBytes Then Return ""
                Dim nonce(NonceBytes - 1) As Byte
                Dim marke(TagBytes - 1) As Byte
                Dim geheim(blob.Length - NonceBytes - TagBytes - 1) As Byte
                Buffer.BlockCopy(blob, 0, nonce, 0, NonceBytes)
                Buffer.BlockCopy(blob, NonceBytes, marke, 0, TagBytes)
                Buffer.BlockCopy(blob, NonceBytes + TagBytes, geheim, 0, geheim.Length)
                Dim klar(geheim.Length - 1) As Byte
                Using aes = New AesGcm(GetKey(), TagBytes)
                    aes.Decrypt(nonce, geheim, marke, klar)
                End Using
                Return Encoding.UTF8.GetString(klar)
            Catch ex As Exception
                ' Angefasst, abgeschnitten oder mit einem anderen Geheimnis geschrieben: dann gibt es
                ' den Wert eben nicht mehr. Der Nutzer traegt ihn neu ein - besser als eine
                ' Zeichenkette weiterzureichen, die niemand angenommen hat.
                DiagnosticLogService.LogException("SecretProtection.Unprotect", ex)
                Return ""
            End Try
        End Function

    End Class

End Namespace
