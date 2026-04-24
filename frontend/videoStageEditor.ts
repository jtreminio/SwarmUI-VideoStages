import {
    CLIP_AUDIO_UPLOAD_FIELD,
    CLIP_AUDIO_UPLOAD_LABEL,
    normalizeUploadFileName,
} from "./constants";
import {
    attachEventListeners,
    type DomEventsDeps,
    handleFieldChange,
} from "./domEvents";
import { captureFocus, restoreFocus } from "./focusRestore";
import { createGenerateWrap } from "./generateWrap";
import { buildDefaultClip } from "./normalization";
import { createObservers } from "./observers";
import {
    ensureClipsSeeded,
    getClips,
    getState,
    type PersistenceCallbacks,
    saveClips,
    saveState,
} from "./persistence";
import { createRefUploadCache } from "./refUploadCache";
import { renderClipCard } from "./renderHtml";
import { getDefaultStageModel, getRootDefaults } from "./rootDefaults";
import { seedRegisteredDimensionsFromCore } from "./swarmInputs";
import type { Clip, VideoStagesConfig } from "./types";
import { validateClips } from "./validation";

export interface VideoStageEditor {
    init(): void;
    startGenerateWrapRetry(intervalMs?: number): void;
}

export function videoStageEditor(): VideoStageEditor {
    let editor: HTMLElement | null = null;
    let clipsRefreshTimer: ReturnType<typeof setTimeout> | null = null;

    const refUploadCache = createRefUploadCache();
    const persistenceCallbacks: PersistenceCallbacks = {};

    const getEditorState = (): VideoStagesConfig => getState();
    const saveEditorState = (state: VideoStagesConfig): void =>
        saveState(state, persistenceCallbacks);
    const getEditorClips = (): Clip[] => getClips();
    const saveEditorClips = (clips: Clip[]): void =>
        saveClips(clips, persistenceCallbacks);

    const scheduleClipsRefresh = (): void => {
        if (clipsRefreshTimer) {
            clearTimeout(clipsRefreshTimer);
        }
        clipsRefreshTimer = setTimeout(() => {
            clipsRefreshTimer = null;
            try {
                renderClips();
            } catch {}
        }, 0);
    };

    const observers = createObservers({
        scheduleRefresh: scheduleClipsRefresh,
        getState: getEditorState,
        saveState: saveEditorState,
    });

    persistenceCallbacks.onAfterSerialize = (serialized: string): void => {
        observers.markPersisted(serialized);
    };

    const generateWrap = createGenerateWrap({ getClips: getEditorClips });

    const createEditor = (preferredRoot?: HTMLElement | null): void => {
        let el =
            preferredRoot instanceof HTMLElement && preferredRoot.isConnected
                ? preferredRoot
                : editor?.isConnected
                  ? editor
                  : null;
        if (!el) {
            const groupContent = document.getElementById(
                "input_group_content_videostages",
            );
            const existingEditors = groupContent?.querySelectorAll<HTMLElement>(
                "#videostages_stage_editor",
            );
            el =
                existingEditors && existingEditors.length > 0
                    ? existingEditors[existingEditors.length - 1]
                    : null;
        }
        if (!el) {
            el = document.createElement("div");
            el.id = "videostages_stage_editor";
            el.className = "videostages-stage-editor keep_group_visible";
            document
                .getElementById("input_group_content_videostages")
                ?.appendChild(el);
        }

        el.style.width = "100%";
        el.style.maxWidth = "100%";
        el.style.minWidth = "0";
        el.style.flex = "1 1 100%";
        el.style.overflow = "visible";
        editor = el;
    };

    const getDomDeps = (): DomEventsDeps => ({
        ensureEditorRoot: createEditor,
        getEditor: () => editor,
        getClips: getEditorClips,
        saveClips: saveEditorClips,
        getState: getEditorState,
        saveState: saveEditorState,
        persistenceCallbacks,
        scheduleClipsRefresh,
        refUploadCache,
    });

    const restoreClipAudioUploadPreviews = (clips: Clip[]): void => {
        if (!editor) {
            return;
        }
        for (let clipIdx = 0; clipIdx < clips.length; clipIdx++) {
            const upload = clips[clipIdx].uploadedAudio;
            if (!upload?.data) {
                continue;
            }
            const input = editor.querySelector<HTMLInputElement>(
                `.auto-file[data-clip-field="${CLIP_AUDIO_UPLOAD_FIELD}"][data-clip-idx="${clipIdx}"]`,
            );
            if (!input) {
                continue;
            }
            if (
                input.dataset.filedata === upload.data &&
                normalizeUploadFileName(input.dataset.filename) ===
                    upload.fileName
            ) {
                continue;
            }
            setMediaFileDirect(
                input,
                upload.data,
                "audio",
                upload.fileName ?? CLIP_AUDIO_UPLOAD_LABEL,
                upload.fileName ?? undefined,
            );
        }
    };

    const renderClips = (): string[] => {
        createEditor();
        if (!editor) {
            return [];
        }

        seedRegisteredDimensionsFromCore();

        const state = getEditorState();
        let clips = state.clips;
        if (clips.length === 0) {
            state.clips = [
                buildDefaultClip(0, getRootDefaults, getDefaultStageModel),
            ];
            clips = state.clips;
            saveEditorState(state);
        }

        const focusSnapshot = captureFocus();
        editor.innerHTML = "";

        const stack = document.createElement("div");
        stack.className = "vs-clip-stack";
        stack.setAttribute("data-vs-clip-stack", "true");
        editor.appendChild(stack);

        if (clips.length === 0) {
            stack.insertAdjacentHTML(
                "beforeend",
                `<div class="vs-empty-card">No video clips. Click "+ Add Video Clip" below.</div>`,
            );
        } else {
            for (let i = 0; i < clips.length; i++) {
                stack.insertAdjacentHTML(
                    "beforeend",
                    renderClipCard(clips[i], i, clips.length, getRootDefaults),
                );
            }
        }

        const addClipButton = document.createElement("button");
        addClipButton.type = "button";
        addClipButton.className = "vs-add-btn vs-add-btn-clip";
        addClipButton.dataset.clipAction = "add-clip";
        addClipButton.innerText = "+ Add Video Clip";
        editor.appendChild(addClipButton);

        attachEventListeners(getDomDeps());
        enableSlidersIn(editor);
        restoreClipAudioUploadPreviews(clips);
        refUploadCache.restorePreviews(editor, clips);
        restoreFocus(focusSnapshot);

        return validateClips(clips);
    };

    const init = (): void => {
        createEditor();
        observers.startClipsInputSync();
        ensureClipsSeeded(persistenceCallbacks);
        generateWrap.tryWrap();
        renderClips();
        observers.installSourceDropdownObserver();
        observers.installBase2EditStageChangeListener();
        observers.installRootVideoTimingChangeListener();
        observers.installRefSourceFallbackListener(createEditor, (target) => {
            handleFieldChange(target, getDomDeps());
        });
    };

    const startGenerateWrapRetry = (intervalMs = 250): void => {
        generateWrap.startRetry(intervalMs);
    };

    return {
        init,
        startGenerateWrapRetry,
    };
}
