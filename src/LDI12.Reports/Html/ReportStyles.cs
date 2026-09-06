namespace LDI12.Reports.Html
{
    /// <summary>
    /// Feuille de style commune aux deux rapports.
    /// </summary>
    /// <remarks>
    /// Trois contraintes commandent ce fichier.
    ///
    /// 1. <b>Un seul fichier.</b> Aucune ressource externe : pas de police téléchargée, pas
    ///    d'image, pas de feuille liée. Un rapport doit s'ouvrir sur le poste d'un client qui n'a
    ///    pas Internet, et rester lisible cinq ans plus tard sur une machine qu'on ne connaît pas.
    ///
    /// 2. <b>Document clair.</b> L'application est sombre, le rapport ne l'est pas : il est fait
    ///    pour être imprimé ou converti en PDF, et un aplat noir sur une page A4 est un gâchis
    ///    d'encre autant qu'une faute de goût. L'identité tient au rouge, à la typographie et à
    ///    la mise en page, pas au fond.
    ///
    /// 3. <b>Impression prévisible.</b> Les couleurs d'état sont conservées à l'impression
    ///    (<c>print-color-adjust</c>), et rien ne doit se couper au milieu d'un constat : un
    ///    diagnostic scindé sur deux pages se lit mal et se conteste facilement.
    ///
    /// Cette troisième contrainte n'est pas décorative : l'impression navigateur est la
    /// <b>seule</b> voie vers le PDF, un second moteur de rendu ayant été écarté. Ce qui se
    /// règle ici ne se rattrape nulle part ailleurs.
    /// </remarks>
    internal static class ReportStyles
    {
        public const string BrandRed = "#BE2129";
        public const string AccentRed = "#DE2028";

        public const string Good = "#047857";
        public const string Warning = "#B45309";
        public const string Problem = "#C2410C";
        public const string Critical = "#BE2129";
        public const string Info = "#2563EB";
        public const string Muted = "#6B6873";

        public const string Css = @"
*{box-sizing:border-box}
html{-webkit-print-color-adjust:exact;print-color-adjust:exact}
body{margin:0;padding:32px 24px 64px;background:#F4F3F7;color:#131116;
     font-family:'Segoe UI',system-ui,-apple-system,'Helvetica Neue',Arial,sans-serif;
     font-size:13px;line-height:1.55}
.sheet{max-width:1000px;margin:0 auto}

h1,h2,h3{margin:0;font-weight:600;line-height:1.25;text-wrap:balance}
h1{font-size:26px}
h2{font-size:17px;margin:34px 0 12px}
h3{font-size:14px}
p{margin:0}
a{color:#BE2129}

.eyebrow{font-size:10.5px;font-weight:600;letter-spacing:.09em;text-transform:uppercase;color:#86838F}
.muted{color:#6B6873}
.tiny{font-size:11.5px}
.num{font-variant-numeric:tabular-nums}

.masthead{display:flex;align-items:flex-start;gap:16px;padding-bottom:20px;border-bottom:3px solid #DE2028}
.mark{flex:0 0 auto;width:38px;height:42px}
.masthead .grow{flex:1 1 auto;min-width:0}
.masthead .issued{text-align:right;white-space:nowrap}

.meta{display:flex;flex-wrap:wrap;gap:0 28px;margin-top:14px}
.meta div{padding:6px 0}
.meta dt{margin:0;font-size:10.5px;font-weight:600;letter-spacing:.08em;text-transform:uppercase;color:#86838F}
.meta dd{margin:2px 0 0;font-size:13px}

.card{background:#fff;border:1px solid #E4E1E9;border-radius:12px;padding:20px;margin:12px 0;
      break-inside:avoid;page-break-inside:avoid}
.card.tight{padding:14px 16px}

.banner{display:flex;gap:12px;align-items:flex-start;border-radius:12px;padding:14px 16px;margin:12px 0;
        border:1px solid;break-inside:avoid}
.banner.warn{background:#FDF6EC;border-color:#B45309;color:#131116}
.banner.info{background:#EEF3FE;border-color:#2563EB;color:#131116}

.score{display:flex;gap:26px;align-items:center}
.score .dial{position:relative;flex:0 0 auto;width:132px;height:132px}
.score .dial .value{position:absolute;inset:0;display:flex;flex-direction:column;
                    align-items:center;justify-content:center}
.score .dial .value b{font-size:40px;font-weight:600;line-height:1;font-variant-numeric:tabular-nums}
.score .dial .value span{font-size:11.5px;color:#6B6873;margin-top:2px}
.arc{display:block}
.band{font-size:19px;font-weight:600;margin-top:5px}

.dims{display:grid;grid-template-columns:repeat(3,1fr);gap:10px;margin-top:12px}
.dim{background:#fff;border:1px solid #E4E1E9;border-radius:12px;padding:14px 16px;break-inside:avoid}
.dim .top{display:flex;justify-content:space-between;align-items:baseline;gap:10px}
.dim .top b{font-size:17px;font-weight:600;font-variant-numeric:tabular-nums}
.track{height:4px;border-radius:2px;background:#E9E6EE;margin:10px 0 8px;overflow:hidden}
.track i{display:block;height:100%;border-radius:2px}

.finding{background:#fff;border:1px solid #E4E1E9;border-radius:12px;padding:0;margin:10px 0;
         display:flex;gap:0;break-inside:avoid;page-break-inside:avoid}
.finding .stripe{flex:0 0 3px;margin:16px 0 16px 15px;border-radius:2px}
.finding .body{flex:1 1 auto;min-width:0;padding:16px 20px 16px 13px}
.finding .head{display:flex;justify-content:space-between;gap:16px;align-items:baseline}
.finding .head b{font-weight:600}
.finding .tag{flex:0 0 auto;font-size:11.5px;font-weight:600;white-space:nowrap}
.finding .tag span{color:#86838F;font-weight:400;margin-left:9px}
.finding .detail{margin-top:7px}
.finding .plain{margin-top:9px;padding:11px 13px;background:#F7F6F9;border-radius:8px;color:#3D3A44}
.evidence{margin:9px 0 0;padding:0;list-style:none}
.evidence li{font-size:11.5px;color:#6B6873;padding:2px 0}

/* Journal d'intervention : une ligne par opération, lisible à la suite. */
.log{margin:10px 0 0;padding:0;list-style:none}
.log li{padding:6px 0;border-top:1px solid #EFEDF2}
.log li:first-child{border-top:none}

.actions{counter-reset:step}
.action{display:flex;gap:14px;background:#fff;border:1px solid #E4E1E9;border-radius:12px;
        padding:15px 18px;margin:8px 0;break-inside:avoid}
.action .rank{flex:0 0 auto;width:24px;font-size:17px;font-weight:600;color:#86838F;
              font-variant-numeric:tabular-nums}
.action .grow{flex:1 1 auto;min-width:0}
.action .side{flex:0 0 auto;text-align:right;font-size:11.5px}

table{width:100%;border-collapse:collapse;margin-top:10px;font-size:11.5px}
th{text-align:left;font-weight:600;color:#6B6873;padding:6px 10px;border-bottom:1px solid #E4E1E9;
   white-space:nowrap}
td{padding:6px 10px;border-bottom:1px solid #F0EEF3;vertical-align:top;font-variant-numeric:tabular-nums}
tr:last-child td{border-bottom:0}
tr.warn td{background:#FDF6EC}
tr.bad td{background:#FCEFEC}
.wrap{overflow-x:auto}

.facts{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:0 28px}
.fact{display:flex;justify-content:space-between;gap:14px;padding:6px 0;border-bottom:1px solid #F0EEF3}
.fact dt{margin:0;color:#6B6873;flex:0 0 auto;max-width:52%}
.fact dd{margin:0;text-align:right;font-variant-numeric:tabular-nums;min-width:0;word-break:break-word}
.fact.absent dd{color:#86838F;font-style:italic}
.fact .why{display:block;font-size:11px;color:#86838F;font-style:normal;margin-top:1px}

.good{color:#047857}
.warn{color:#B45309}
.bad{color:#C2410C}
.crit{color:#BE2129}
.info{color:#2563EB}

.foot{margin-top:36px;padding-top:16px;border-top:1px solid #E4E1E9;font-size:11.5px;color:#86838F}

@media (max-width:760px){
  .dims{grid-template-columns:1fr}
  .facts{grid-template-columns:1fr}
  .score{flex-direction:column;align-items:flex-start}
  .masthead{flex-wrap:wrap}
  .masthead .issued{text-align:left}
}

@media print{
  body{background:#fff;padding:0;font-size:11pt}
  .card,.dim,.finding,.action{border-color:#D6D2DC}
  h2{margin-top:22px}
  .foot{page-break-inside:avoid}

  /* Un constat scindé sur deux pages se lit mal et se conteste facilement : il porte son
     verdict d'un côté de la coupure et sa preuve de l'autre. Les cadres de caractéristiques,
     eux, dépassent souvent une page entière, leur interdire la coupure laisserait un bas de
     page vide. */
  .finding,.action{page-break-inside:avoid;break-inside:avoid}
  tr{page-break-inside:avoid;break-inside:avoid}
  h2,h3{page-break-after:avoid;break-after:avoid}

  /* Un tableau qui court sur deux pages perd ses en-têtes de colonnes : les chiffres de la
     seconde page ne veulent plus rien dire. */
  thead{display:table-header-group}
  @page{margin:14mm}
}
";
    }
}
