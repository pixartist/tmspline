using System.Net;

namespace TmSpline.Cloud;

/// <summary>Who runs the site (Site:Operator), from site.json next to the data folder: not in the repository.</summary>
public sealed class SiteOperator
{
    public string Name { get; set; } = "";
    /// <summary>Postal address, one entry per line.</summary>
    public string[] Address { get; set; } = [];
    public string Email { get; set; } = "";
    /// <summary>How long authentik keeps its event log (login events with IP addresses), as shown on the privacy page.</summary>
    public string AuthEventRetention { get; set; } = "";

    public bool Complete => Name.Length > 0 && Address.Length > 0 && Email.Length > 0;
}

/// <summary>Impressum (§ 5 DDG) and Datenschutzerklärung: public pages, in German like the law they follow.</summary>
public static class LegalPages
{
    private static string E(string s) => WebUtility.HtmlEncode(s);

    private static string Layout(string title, string body) => $$"""
        <!doctype html>
        <html lang="de">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <title>{{E(title)}} · TMSpline</title>
          <link rel="stylesheet" href="/site.css" />
        </head>
        <body>
          <canvas id="scene" aria-hidden="true"></canvas>
          <header class="bar"><a class="brand" href="/">TMSpline</a></header>
          <main>
            <article class="card legal">
              {{body}}
            </article>
            <footer><a href="/impressum">Impressum</a> · <a href="/datenschutz">Datenschutz</a></footer>
          </main>
          <script src="/background.js"></script>
        </body>
        </html>
        """;

    private static string Contact(SiteOperator o) => o.Complete
        ? $"""<p>{E(o.Name)}<br />{string.Join("<br />", o.Address.Select(E))}</p><p>E-Mail: <a href="mailto:{E(o.Email)}">{E(o.Email)}</a></p>"""
        : """<p class="muted">(Die Angaben zum Betreiber sind noch nicht eingetragen.)</p>""";

    public static string Impressum(SiteOperator o) => Layout("Impressum", $"""
        <h1>Impressum</h1>
        <h2>Angaben gemäß § 5 DDG</h2>
        {Contact(o)}
        <h2>Verantwortlich für den Inhalt</h2>
        <p>{(o.Complete ? E(o.Name) + ", Anschrift wie oben." : "")}</p>
        <h2>Hinweis</h2>
        <p>TMSpline ist ein privates, nicht kommerzielles Projekt und steht nicht in Verbindung mit Nadeo oder Ubisoft.
          Trackmania ist eine Marke von Ubisoft Entertainment. Auf dieser Seite werden keine Inhalte des Spiels
          bereitgestellt.</p>
        """);

    public static string Datenschutz(SiteOperator o) => Layout("Datenschutz", $"""
        <h1>Datenschutzerklärung</h1>

        <h2>Verantwortlicher</h2>
        {Contact(o)}

        <h2>Wo die Daten liegen</h2>
        <p>TMSpline und die Anmeldung (authentik) laufen auf eigenen Rechnern des Betreibers in Deutschland. Es gibt
          keinen externen Hoster, keine Werbung, keine Analyse- oder Tracking-Dienste und keine eingebundenen Inhalte
          fremder Server (Schriften, Skripte, Bilder).</p>

        <h2>Konto und Anmeldung</h2>
        <p>Ein Konto gibt es nur auf Einladung. Bei der Registrierung und Anmeldung über authentik werden
          Benutzername, E-Mail-Adresse und das Passwort (nur als Hash) gespeichert. authentik protokolliert
          Anmeldungen und sicherheitsrelevante Ereignisse mit Zeitpunkt und IP-Adresse, um Missbrauch zu erkennen;
          diese Einträge werden {(o.AuthEventRetention.Length > 0 ? "nach " + E(o.AuthEventRetention) : "regelmäßig")}
          gelöscht. Rechtsgrundlage ist Art. 6 Abs. 1 lit. b DSGVO (Bereitstellung des Dienstes) und für das
          Sicherheitsprotokoll Art. 6 Abs. 1 lit. f DSGVO (Schutz des Dienstes).</p>

        <h2>Was TMSpline speichert</h2>
        <ul>
          <li>die Kennung deines Kontos, deinen Benutzernamen und wann du TMSpline zuletzt benutzt hast;</li>
          <li>die Strecken, die du in der App speicherst, und die Freigabelinks, die du erstellst. Eine freigegebene
            Strecke sehen alle angemeldeten Nutzer, mit deinem Benutzernamen als Ersteller, bis du die Freigabe
            beendest oder die Strecke löschst.</li>
        </ul>
        <p>Die Daten bleiben gespeichert, bis du sie löschst oder die Löschung deines Kontos verlangst.
          Rechtsgrundlage ist Art. 6 Abs. 1 lit. b DSGVO.</p>

        <h2>Server-Protokolle</h2>
        <p>Beim Aufruf verarbeiten die Server technisch nötige Daten wie IP-Adresse und aufgerufene Adresse, um die
          Seite auszuliefern. Zugriffsprotokolle mit IP-Adressen werden dabei nicht gespeichert; das
          Fehlerprotokoll von TMSpline enthält keine IP-Adressen.</p>

        <h2>Cookies</h2>
        <p>Es werden nur technisch nötige Cookies gesetzt: das Anmelde-Cookie von TMSpline (bis zu 30 Tage) und das
          Sitzungs-Cookie von authentik. Eine Einwilligung ist dafür nicht erforderlich (§ 25 Abs. 2 TDDDG).</p>

        <h2>Die Desktop-App</h2>
        <p>Die App spricht für Anmeldung, Strecken und Update-Prüfung nur mit diesem Server. Beim ersten Export lädt
          sie auf deine Bestätigung hin den NadeoImporter direkt von Nadeos Server
          (nadeo-download.cdn.ubi.com) herunter; dabei erhält Ubisoft wie bei jedem Download deine IP-Adresse.
          Alles, was die App aus deinem Spiel liest (Blockbibliothek, Karten), bleibt auf deinem Rechner.</p>

        <h2>Deine Rechte</h2>
        <p>Du hast das Recht auf Auskunft, Berichtigung, Löschung, Einschränkung der Verarbeitung,
          Datenübertragbarkeit und Widerspruch (Art. 15–21 DSGVO). Schreib dafür einfach eine E-Mail an die oben
          genannte Adresse. Außerdem kannst du dich bei einer Datenschutz-Aufsichtsbehörde beschweren.</p>
        """);
}
