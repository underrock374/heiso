// 画面の言語(HEISO)。訳は csharp/Fh6.Core/i18n/strings.json の 1 ファイルだけにあり、各アプリがページの読み込みより前に
// window.HEISO_I18N = { lang: "ja" | "en", strings: { 日本語の文: 訳 }, region: Windows の地域(再生アプリの単位の自動に使う) } として渡す(Fh6.Core の Strings.Dictionary)。
// このファイルも Core にだけ置き、ビルドのときに各アプリの画面のフォルダへ出す。
//
// - t(日本語の文, { 名前: 値 }): 今の言語の文(日本語の文は、ふつうの "…" の文字列でそのまま書く。訳し漏れのテストが拾えるように)。辞書に無ければ日本語のまま。{名前} に値を、{名前:F1} なら小数 1 桁で差し込む
// - HTML の決まった文言には印を付ける: data-i18n(文字。中身の日本語が鍵。子の要素を持たない要素だけに付ける)、
//   data-i18n-title(title)、data-i18n-placeholder(placeholder)。開いたときに applyI18n() が置き換える
// - 数値は地域の形式によらず、いつもピリオドで書く(C# の Strings.T と同じ)
(function () {
  const I = window.HEISO_I18N || { lang: "ja", strings: {} };

  function fill(text, params) {
    if (!params) return text;
    return text.replace(/\{(\w+)(?::([^{}]+))?\}/g, (m, name, fmt) => {
      if (!(name in params)) return m;
      const v = params[name];
      const f = fmt && /^F(\d+)$/i.exec(fmt);
      return typeof v === "number" && f ? v.toFixed(Number(f[1])) : String(v);
    });
  }

  window.t = (key, params) => fill(I.strings[key] || key, params);
  window.i18nLang = I.lang;

  window.applyI18n = (root = document) => {
    for (const el of root.querySelectorAll("[data-i18n]")) {
      if (el.dataset.i18nKey == null) el.dataset.i18nKey = el.textContent.trim();
      el.textContent = t(el.dataset.i18nKey);
    }
    for (const el of root.querySelectorAll("[data-i18n-title]")) {
      if (el.dataset.i18nTitleKey == null) el.dataset.i18nTitleKey = el.title;
      el.title = t(el.dataset.i18nTitleKey);
    }
    for (const el of root.querySelectorAll("[data-i18n-placeholder]")) {
      if (el.dataset.i18nPlaceholderKey == null) el.dataset.i18nPlaceholderKey = el.placeholder;
      el.placeholder = t(el.dataset.i18nPlaceholderKey);
    }
    document.documentElement.lang = I.lang;
  };

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", () => applyI18n());
  else applyI18n();
})();
