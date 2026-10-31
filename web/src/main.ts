import "./style.css";
import { loadCore } from "./core";
import { Editor } from "./editor";
import { createControlsHelp } from "./help";
import { createUi, loadSavedMap } from "./ui";

const viewport = document.getElementById("viewport")!;
const loading = document.getElementById("loading")!;

loadCore()
  .then(() => {
    loading.remove();
    const editor = new Editor(viewport, loadSavedMap());
    createUi(editor, document.getElementById("toolbar")!, document.getElementById("inspector")!, document.getElementById("status")!);
    createControlsHelp(viewport);
    editor.frameTrack();
    // Handy for debugging from the browser console during development.
    if (import.meta.env.DEV) Object.assign(window, { editor });
  })
  .catch((err) => {
    loading.textContent = `Could not start the track engine: ${err}`;
    console.error(err);
  });
