// C#(MainWindow)との通信。host.postMessage と message の受信は、このファイルにだけ書く(docs/architecture.md のルール 6)
"use strict";

const host = window.chrome.webview;

// メッセージの型(ViewMessage・HostMessage)は C# の型から作った csharp/Fh6.Player.App/messages.d.ts にある(型の検査で使う)

/**
 * C# へメッセージを送る。メッセージの型に無いプロパティを書くと、型の検査で落ちる
 * @template {ViewMessage} T
 * @param {T & ExtraKeys<T>} message
 */
function sendToHost(message) {
  host.postMessage(message);
}

/**
 * C# から届いたメッセージを受ける関数を登録する
 * @param {(message: HostMessage) => void} handler
 */
function onHostMessage(handler) {
  host.addEventListener("message", (e) => handler(e.data));
}
