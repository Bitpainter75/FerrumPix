Imports System
Imports System.Collections.Generic
Imports System.Net
Imports System.Text

Namespace Services

    ''' <summary>Was eine Nachricht sagt, schon in der Sprache des Empfaengers. Jede Nachricht von
    ''' FerrumPix (Kennwort vergessen, Test-E-Mail, kuenftige) fuellt diese Felder; die Form
    ''' kommt aus <see cref="MailTemplate"/>. Leere Felder fallen weg.</summary>
    Public NotInheritable Class MailContent
        ''' <summary>Die Ueberschrift.</summary>
        Public Property Title As String = ""
        ''' <summary>Die Anrede, etwa "Hallo Lena,".</summary>
        Public Property Greeting As String = ""
        ''' <summary>Die Absaetze; ein Zeilenumbruch darin bleibt einer.</summary>
        Public Property Paragraphs As New List(Of String)()
        ''' <summary>Die Beschriftung des Knopfs; ohne sie oder ohne Link gibt es keinen.</summary>
        Public Property ButtonText As String = ""
        ''' <summary>Das Ziel des Knopfs, nur http oder https.</summary>
        Public Property Link As String = ""
        ''' <summary>Der Satz vor dem Link als Text zum Kopieren.</summary>
        Public Property LinkHint As String = ""
        ''' <summary>Kleingedrucktes unter dem Link (Gueltigkeit, "nicht angefordert").</summary>
        Public Property Notes As New List(Of String)()
        ''' <summary>Die Adresse von FerrumPix fuer die Fusszeile, oder leer.</summary>
        Public Property FooterAddress As String = ""
        ''' <summary>Die Sprache fuer lang im HTML, etwa "de".</summary>
        Public Property Language As String = "en"
    End Class

    ''' <summary>
    ''' Die gemeinsame Vorlage aller Nachrichten: einmal als Text (Rueckfall fuer Mailprogramme
    ''' ohne HTML), einmal als HTML.
    '''
    ''' <para>DAS HTML IST FUER MAILPROGRAMME GEBAUT, nicht fuer Browser: Aufbau aus Tabellen,
    ''' alle Angaben als style am Element (viele Programme werfen style-Bloecke weg), hoechstens
    ''' 600 Punkte breit, kein Skript, keine Bilder oder Schriften von aussen. Das Logo kommt als
    ''' Teil der Nachricht (cid:). Kopf dunkel (#0B0E11) mit Akzentstreifen, Inhalt auf Weiss mit
    ''' dunkler Schrift, damit er auch in einem dunkel eingestellten Mailprogramm lesbar bleibt,
    ''' der Knopf im Akzent (#F08A1A) mit dunkler Schrift, weil Weiss auf diesem Orange zu wenig
    ''' Kontrast hat.</para>
    '''
    ''' <para>Jeder Text geht durch HtmlEncode; ein Link, der nicht mit http:// oder https://
    ''' beginnt, wird weder Knopf noch Verweis.</para>
    ''' </summary>
    Public NotInheritable Class MailTemplate

        Private Sub New()
        End Sub

        Private Const Font As String = "font-family:Arial,Helvetica,sans-serif;"

        ''' <summary>Taugt der Text als Ziel eines Verweises?</summary>
        Friend Shared Function IsWebLink(link As String) As Boolean
            Dim parsed As Uri = Nothing
            Return Not String.IsNullOrWhiteSpace(link) AndAlso Uri.TryCreate(link.Trim(), UriKind.Absolute, parsed) AndAlso
                   (parsed.Scheme = Uri.UriSchemeHttps OrElse parsed.Scheme = Uri.UriSchemeHttp)
        End Function

        ''' <summary>Die Nachricht als reiner Text, Zeilenenden CRLF.</summary>
        Public Shared Function PlainText(content As MailContent) As String
            Dim text As New StringBuilder()
            Dim block = Sub(value As String)
                            If String.IsNullOrWhiteSpace(value) Then Return
                            text.Append(value.Trim()).Append(vbLf).Append(vbLf)
                        End Sub
            block(content.Title)
            block(content.Greeting)
            For Each paragraph In content.Paragraphs
                block(paragraph)
            Next
            If IsWebLink(content.Link) Then block(content.Link.Trim())
            For Each note In content.Notes
                block(note)
            Next
            text.Append("--").Append(" "c).Append(vbLf).Append("FerrumPix")
            If IsWebLink(content.FooterAddress) Then text.Append(vbLf).Append(content.FooterAddress.Trim())
            text.Append(vbLf)
            Return text.ToString().Replace(vbCrLf, vbLf).Replace(vbLf, vbCrLf)
        End Function

        ''' <summary>Die Nachricht als HTML. <paramref name="logoContentId"/> ist die Content-ID
        ''' des eingebetteten Logos, Nothing ohne Logo.</summary>
        Public Shared Function Html(content As MailContent, logoContentId As String) As String
            Dim e = Function(value As String) WebUtility.HtmlEncode(If(value, "").Trim()).Replace(vbCrLf, vbLf).Replace(vbLf, "<br>")
            Dim language = If(String.IsNullOrWhiteSpace(content.Language), "en", content.Language.Trim())
            Dim page As New StringBuilder()
            page.Append("<!DOCTYPE html>").Append(vbCrLf)
            page.Append("<html lang=""").Append(WebUtility.HtmlEncode(language)).Append(""">").Append(vbCrLf)
            page.Append("<head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width, initial-scale=1"">")
            page.Append("<meta name=""color-scheme"" content=""light""><meta name=""supported-color-schemes"" content=""light"">")
            page.Append("<title>").Append(e(content.Title)).Append("</title></head>").Append(vbCrLf)
            page.Append("<body style=""margin:0;padding:0;background-color:#E9ECEF;"">").Append(vbCrLf)
            page.Append("<table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0"" style=""background-color:#E9ECEF;"">").Append(vbCrLf)
            page.Append("<tr><td align=""center"" style=""padding:24px 12px;"">").Append(vbCrLf)
            page.Append("<table role=""presentation"" width=""600"" cellpadding=""0"" cellspacing=""0"" border=""0"" style=""width:100%;max-width:600px;background-color:#FFFFFF;border-radius:8px;"">").Append(vbCrLf)

            ' Kopf: Logo und Name auf Dunkel, darunter der Akzentstreifen.
            page.Append("<tr><td style=""background-color:#0B0E11;padding:18px 32px;border-radius:8px 8px 0 0;"">")
            page.Append("<table role=""presentation"" cellpadding=""0"" cellspacing=""0"" border=""0""><tr>")
            If Not String.IsNullOrEmpty(logoContentId) Then
                page.Append("<td style=""vertical-align:middle;padding-right:12px;""><img src=""cid:").Append(WebUtility.HtmlEncode(logoContentId)).
                    Append(""" width=""48"" height=""48"" alt=""FerrumPix"" style=""display:block;border:0;width:48px;height:48px;""></td>")
            End If
            page.Append("<td style=""vertical-align:middle;").Append(Font).Append("font-size:22px;font-weight:bold;color:#E7ECF0;"">FerrumPix</td>")
            page.Append("</tr></table></td></tr>").Append(vbCrLf)
            page.Append("<tr><td style=""height:4px;line-height:4px;font-size:0;background-color:#F08A1A;"">&nbsp;</td></tr>").Append(vbCrLf)

            ' Inhalt.
            page.Append("<tr><td style=""padding:32px;").Append(Font).Append("color:#1F2328;"">").Append(vbCrLf)
            If Not String.IsNullOrWhiteSpace(content.Title) Then
                page.Append("<h1 style=""margin:0 0 20px 0;").Append(Font).Append("font-size:22px;line-height:1.3;color:#0B0E11;"">").Append(e(content.Title)).Append("</h1>").Append(vbCrLf)
            End If
            Dim paragraph = Sub(value As String)
                                If String.IsNullOrWhiteSpace(value) Then Return
                                page.Append("<p style=""margin:0 0 16px 0;").Append(Font).Append("font-size:16px;line-height:1.5;color:#1F2328;"">").Append(e(value)).Append("</p>").Append(vbCrLf)
                            End Sub
            paragraph(content.Greeting)
            For Each value In content.Paragraphs
                paragraph(value)
            Next
            If IsWebLink(content.Link) Then
                Dim href = WebUtility.HtmlEncode(content.Link.Trim())
                If Not String.IsNullOrWhiteSpace(content.ButtonText) Then
                    page.Append("<table role=""presentation"" cellpadding=""0"" cellspacing=""0"" border=""0"" style=""margin:24px 0;""><tr>")
                    page.Append("<td align=""center"" bgcolor=""#F08A1A"" style=""border-radius:6px;background-color:#F08A1A;"">")
                    page.Append("<a href=""").Append(href).Append(""" target=""_blank"" style=""display:inline-block;padding:14px 28px;").Append(Font).
                        Append("font-size:17px;font-weight:bold;line-height:1.2;color:#0B0E11;text-decoration:none;border-radius:6px;"">").
                        Append(e(content.ButtonText)).Append("</a></td></tr></table>").Append(vbCrLf)
                End If
                If Not String.IsNullOrWhiteSpace(content.LinkHint) Then
                    page.Append("<p style=""margin:0 0 6px 0;").Append(Font).Append("font-size:14px;line-height:1.5;color:#57606A;"">").Append(e(content.LinkHint)).Append("</p>").Append(vbCrLf)
                End If
                page.Append("<p style=""margin:0 0 24px 0;").Append(Font).Append("font-size:14px;line-height:1.5;word-break:break-all;"">")
                page.Append("<a href=""").Append(href).Append(""" target=""_blank"" style=""color:#A85500;text-decoration:underline;"">").Append(href).Append("</a></p>").Append(vbCrLf)
            End If
            For Each note In content.Notes
                If String.IsNullOrWhiteSpace(note) Then Continue For
                page.Append("<p style=""margin:0 0 8px 0;").Append(Font).Append("font-size:14px;line-height:1.5;color:#57606A;"">").Append(e(note)).Append("</p>").Append(vbCrLf)
            Next
            page.Append("</td></tr>").Append(vbCrLf)

            ' Fusszeile.
            page.Append("<tr><td style=""padding:18px 32px;background-color:#F3F4F6;border-radius:0 0 8px 8px;").Append(Font).Append("font-size:13px;line-height:1.5;color:#57606A;"">FerrumPix")
            If IsWebLink(content.FooterAddress) Then
                Dim footer = WebUtility.HtmlEncode(content.FooterAddress.Trim())
                page.Append("<br><a href=""").Append(footer).Append(""" target=""_blank"" style=""color:#57606A;text-decoration:underline;"">").Append(footer).Append("</a>")
            End If
            page.Append("</td></tr>").Append(vbCrLf)
            page.Append("</table>").Append(vbCrLf)
            page.Append("</td></tr></table>").Append(vbCrLf)
            page.Append("</body>").Append(vbCrLf).Append("</html>").Append(vbCrLf)
            Return page.ToString()
        End Function

    End Class

End Namespace
