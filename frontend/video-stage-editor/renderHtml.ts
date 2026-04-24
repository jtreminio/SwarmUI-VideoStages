import {
    AUDIO_SOURCE_UPLOAD,
    buildAudioSourceOptions,
    resolveAudioSourceValue,
} from "../AudioSourceController";
import {
    clipFieldId,
    escapeAttr,
    injectFieldData,
    overrideSliderSteps,
    refFieldId,
    renderOptionList,
    stageFieldId,
} from "../RenderUtils";
import {
    type Clip,
    type ImageSourceOption,
    REF_SOURCE_BASE,
    REF_SOURCE_REFINER,
    REF_SOURCE_UPLOAD,
    type RefImage,
    type RootDefaults,
    type Stage,
} from "../Types";
import {
    CLIP_AUDIO_UPLOAD_DESCRIPTION,
    CLIP_AUDIO_UPLOAD_FIELD,
    CLIP_AUDIO_UPLOAD_LABEL,
    CLIP_DURATION_MAX,
    CLIP_DURATION_MIN,
    CLIP_DURATION_SLIDER_MAX,
    CLIP_DURATION_SLIDER_STEP,
    parseBase2EditStageIndex,
    REF_FRAME_MIN,
    STAGE_REF_STRENGTH_DEFAULT,
    STAGE_REF_STRENGTH_MAX,
    STAGE_REF_STRENGTH_MIN,
    STAGE_REF_STRENGTH_STEP,
    stageRefStrengthField,
} from "./constants";
import { getReferenceFrameMax } from "./normalization";
import { getBase2EditStageRefs } from "./swarmInputs";
import { getRefSourceError } from "./validation";

export const decorateAutoInputWrapper = (
    html: string,
    className: string,
    hidden = false,
): string =>
    html.replace(
        /<div class="([^"]*)"([^>]*)>/,
        (_match, classes, attrs) =>
            `<div class="${classes} ${className}"${attrs}${hidden ? ' style="display: none;"' : ""}>`,
    );

export const dropdownOptions = (
    values: string[],
    labels: string[],
    selected: string,
): ImageSourceOption[] => {
    const finalValues = [...values];
    const finalLabels = [...labels];
    if (selected && !finalValues.includes(selected)) {
        finalValues.unshift(selected);
        finalLabels.unshift(selected);
    }
    return finalValues.map((value, idx) => ({
        value,
        label: finalLabels[idx] ?? value,
    }));
};

/**
 * Native select like {@link buildNativeDropdown}, but with strict option matching.
 * `makeDropdownInput` uses loose equality and can select the wrong option after re-render.
 */
export const buildNativeDropdownStrict = (
    id: string,
    paramId: string,
    label: string,
    options: ImageSourceOption[],
    selected: string,
): string => {
    const escapedLabel = escapeAttr(label);
    const selectedStr = `${selected ?? ""}`;
    const optionHtml = renderOptionList(options, selectedStr);
    const baseHtml = `
    <div class="auto-input auto-dropdown-box auto-input-flex">
        <label>
            <span class="auto-input-name">${escapedLabel}</span>
        </label>
        <select class="auto-dropdown" id="${escapeAttr(id)}" data-name="${escapedLabel}" data-param_id="${escapeAttr(paramId)}" autocomplete="off" onchange="autoSelectWidth(this)">
${optionHtml}
        </select>
    </div>`;
    return options.reduce((acc, option) => {
        if (!option.disabled) {
            return acc;
        }
        const optionValue = escapeAttr(option.value);
        return acc.replace(
            new RegExp(`(<option [^>]*value="${optionValue}")`),
            "$1 disabled",
        );
    }, baseHtml);
};

export const buildNativeDropdown = (
    id: string,
    paramId: string,
    label: string,
    options: ImageSourceOption[],
    selected: string,
): string => {
    const values = options.map((option) => option.value);
    const labels = options.map((option) => option.label);
    const html = makeDropdownInput(
        "",
        id,
        paramId,
        label,
        "",
        values,
        selected,
        false,
        false,
        labels,
        false,
    );
    return options.reduce((acc, option) => {
        if (!option.disabled) {
            return acc;
        }
        const optionValue = escapeAttr(option.value);
        return acc.replace(
            new RegExp(`(<option [^>]*value="${optionValue}")`),
            "$1 disabled",
        );
    }, html);
};

export const buildRefSourceOptions = (
    currentValue: string,
): ImageSourceOption[] => {
    const options: ImageSourceOption[] = [
        { value: REF_SOURCE_BASE, label: "Base Output" },
        { value: REF_SOURCE_REFINER, label: "Refiner Output" },
        { value: REF_SOURCE_UPLOAD, label: "Upload" },
    ];
    for (const editRef of getBase2EditStageRefs()) {
        const editStage = parseBase2EditStageIndex(editRef);
        options.push({
            value: editRef,
            label: `Base2Edit Edit ${editStage} Output`,
        });
    }
    if (currentValue && !options.some((o) => o.value === currentValue)) {
        const isBase2Edit = parseBase2EditStageIndex(currentValue) != null;
        options.unshift({
            value: currentValue,
            label: isBase2Edit
                ? `Missing Base2Edit ${currentValue}`
                : currentValue,
            disabled: isBase2Edit,
        });
    }
    return options;
};

export const renderClipAudioUploadField = (
    clip: Clip,
    clipIdx: number,
    audioSource: string,
): string => {
    const id = clipFieldId(clipIdx, CLIP_AUDIO_UPLOAD_FIELD);
    return decorateAutoInputWrapper(
        injectFieldData(
            makeAudioInput(
                "",
                id,
                CLIP_AUDIO_UPLOAD_FIELD,
                CLIP_AUDIO_UPLOAD_LABEL,
                CLIP_AUDIO_UPLOAD_DESCRIPTION,
                false,
                true,
                true,
                true,
            ),
            {
                "data-clip-field": CLIP_AUDIO_UPLOAD_FIELD,
                "data-clip-idx": String(clipIdx),
                "data-has-uploaded-audio": clip.uploadedAudio?.data
                    ? "true"
                    : "false",
            },
        ),
        "vs-clip-audio-upload-field",
        audioSource !== AUDIO_SOURCE_UPLOAD,
    );
};

export const renderRefRow = (
    ref: RefImage,
    clip: Clip,
    clipIdx: number,
    refIdx: number,
    getRootDefaults: () => RootDefaults,
): string => {
    const collapseTitle = ref.expanded ? "Collapse" : "Expand";
    const collapseGlyph = ref.expanded ? "&#x2B9F;" : "&#x2B9E;";
    const head = `
            <div class="vs-card-head">
                <button type="button" class="basic-button vs-btn-tiny vs-btn-collapse" data-ref-action="toggle-collapse" data-ref-idx="${refIdx}" data-clip-idx="${clipIdx}" title="${collapseTitle}">${collapseGlyph}</button>
                <div class="vs-card-title">Ref Image ${refIdx}</div>
                <div class="vs-card-actions">
                    <button type="button" class="interrupt-button vs-btn-tiny" data-ref-action="delete" data-ref-idx="${refIdx}" data-clip-idx="${clipIdx}" title="Remove reference">&times;</button>
                </div>
            </div>
        `;
    if (!ref.expanded) {
        return `<section class="vs-card vs-ref-card input-group" data-ref-idx="${refIdx}">${head}</section>`;
    }

    const sourceOptions = buildRefSourceOptions(ref.source);
    const frameCount = getReferenceFrameMax(getRootDefaults, clip);
    const sourceError = getRefSourceError(ref.source);
    const errorHtml = sourceError
        ? `<div class="vs-field-error">${escapeAttr(sourceError)}</div>`
        : "";

    const sourceField = injectFieldData(
        buildNativeDropdown(
            refFieldId(clipIdx, refIdx, "source"),
            "source",
            "Image Source",
            sourceOptions,
            ref.source,
        ),
        {
            "data-ref-field": "source",
            "data-ref-idx": String(refIdx),
            "data-clip-idx": String(clipIdx),
        },
    );

    const uploadField = decorateAutoInputWrapper(
        injectFieldData(
            makeImageInput(
                "",
                refFieldId(clipIdx, refIdx, "uploadFileName"),
                "uploadFileName",
                "Upload Image",
                "",
                false,
                false,
                true,
                false,
            ),
            {
                "data-ref-field": "uploadFileName",
                "data-ref-idx": String(refIdx),
                "data-clip-idx": String(clipIdx),
            },
        ),
        "vs-ref-upload-field",
        ref.source !== REF_SOURCE_UPLOAD,
    );

    const frameField = injectFieldData(
        makeSliderInput(
            "",
            refFieldId(clipIdx, refIdx, "frame"),
            "frame",
            `Frame (max ${frameCount})`,
            "",
            String(ref.frame),
            REF_FRAME_MIN,
            frameCount,
            REF_FRAME_MIN,
            frameCount,
            1,
            false,
            false,
            false,
        ),
        {
            "data-ref-field": "frame",
            "data-ref-idx": String(refIdx),
            "data-clip-idx": String(clipIdx),
        },
    );

    const fromEndField = injectFieldData(
        makeCheckboxInput(
            "",
            refFieldId(clipIdx, refIdx, "fromEnd"),
            "fromEnd",
            "Count in reverse from end",
            "",
            ref.fromEnd,
            false,
            false,
            false,
        ),
        {
            "data-ref-field": "fromEnd",
            "data-ref-idx": String(refIdx),
            "data-clip-idx": String(clipIdx),
        },
    );

    return `<section class="vs-card vs-ref-card input-group" data-ref-idx="${refIdx}">
            ${head}
            <div class="vs-card-body input-group-content">
                ${sourceField}
                ${uploadField}
                ${frameField}
                ${fromEndField}
                ${errorHtml}
            </div>
        </section>`;
};

export const renderStageRow = (
    clip: Clip,
    stage: Stage,
    clipIdx: number,
    stageIdx: number,
    getRootDefaults: () => RootDefaults,
): string => {
    const cardClasses = ["vs-card", "input-group"];
    if (stage.skipped) {
        cardClasses.push("vs-skipped");
    }
    const collapseTitle = stage.expanded ? "Collapse" : "Expand";
    const collapseGlyph = stage.expanded ? "&#x2B9F;" : "&#x2B9E;";
    const skipTitle = stage.skipped ? "Re-enable stage" : "Skip stage";
    const skipBtnVariant = stage.skipped ? "vs-btn-skip-active" : "";
    const head = `
            <div class="vs-card-head">
                <button type="button" class="basic-button vs-btn-tiny vs-btn-collapse" data-stage-action="toggle-collapse" data-stage-idx="${stageIdx}" data-clip-idx="${clipIdx}" title="${collapseTitle}">${collapseGlyph}</button>
                <div class="vs-card-title">Stage ${stageIdx}</div>
                <div class="vs-card-actions">
                    <button type="button" class="basic-button vs-btn-tiny ${skipBtnVariant}" data-stage-action="skip" data-stage-idx="${stageIdx}" data-clip-idx="${clipIdx}" title="${skipTitle}">&#x23ED;&#xFE0E;</button>
                    <button type="button" class="interrupt-button vs-btn-tiny" data-stage-action="delete" data-stage-idx="${stageIdx}" data-clip-idx="${clipIdx}" title="Remove stage" ${clip.stages.length === 1 ? "disabled" : ""}>&times;</button>
                </div>
            </div>
        `;
    if (!stage.expanded) {
        return `<section class="${cardClasses.join(" ")}" data-stage-idx="${stageIdx}">${head}</section>`;
    }

    const defaults = getRootDefaults();
    const stageSliderField = (
        field: string,
        label: string,
        value: number,
        min: number,
        max: number,
        step: number,
        disabled = false,
    ): string => {
        let html = injectFieldData(
            makeSliderInput(
                "",
                stageFieldId(clipIdx, stageIdx, field),
                field,
                label,
                "",
                String(value),
                min,
                max,
                min,
                max,
                step,
                false,
                false,
                false,
            ),
            {
                "data-stage-field": field,
                "data-stage-idx": String(stageIdx),
                "data-clip-idx": String(clipIdx),
            },
        );
        if (disabled) {
            html = html.replace(
                /<input class="auto-slider-number nogrow"/g,
                '<input class="auto-slider-number nogrow" disabled',
            );
            html = html.replace(
                /<input class="auto-slider-range nogrow"/g,
                '<input class="auto-slider-range nogrow" disabled',
            );
        }
        return html;
    };
    const stageDropdownField = (
        field: string,
        label: string,
        values: string[],
        labels: string[],
        selected: string,
        disabled = false,
    ): string => {
        let html = injectFieldData(
            buildNativeDropdown(
                stageFieldId(clipIdx, stageIdx, field),
                field,
                label,
                dropdownOptions(values, labels, selected),
                selected,
            ),
            {
                "data-stage-field": field,
                "data-stage-idx": String(stageIdx),
                "data-clip-idx": String(clipIdx),
            },
        );
        if (disabled) {
            html = html.replace(/<select /, "<select disabled ");
        }
        return html;
    };

    const modelField = stageDropdownField(
        "model",
        "Model",
        defaults.modelValues,
        defaults.modelLabels,
        stage.model,
    );
    const controlField = stageSliderField(
        "control",
        "Control",
        stage.control,
        defaults.controlMin,
        defaults.controlMax,
        defaults.controlStep,
    );
    const stepsField = stageSliderField(
        "steps",
        "Steps",
        stage.steps,
        defaults.stepsMin,
        defaults.stepsMax,
        defaults.stepsStep,
    );
    const cfgScaleField = stageSliderField(
        "cfgScale",
        "CFG Scale",
        stage.cfgScale,
        defaults.cfgScaleMin,
        defaults.cfgScaleMax,
        defaults.cfgScaleStep,
    );
    const upscaleField = stageSliderField(
        "upscale",
        "Upscale",
        stage.upscale,
        defaults.upscaleMin,
        defaults.upscaleMax,
        defaults.upscaleStep,
        stageIdx === 0,
    );
    const upscaleMethodField = (() => {
        const selectedMethod = `${stage.upscaleMethod ?? ""}`;
        let html = injectFieldData(
            buildNativeDropdownStrict(
                stageFieldId(clipIdx, stageIdx, "upscaleMethod"),
                "upscaleMethod",
                "Upscale Method",
                dropdownOptions(
                    defaults.upscaleMethodValues,
                    defaults.upscaleMethodLabels,
                    selectedMethod,
                ),
                selectedMethod,
            ),
            {
                "data-stage-field": "upscaleMethod",
                "data-stage-idx": String(stageIdx),
                "data-clip-idx": String(clipIdx),
            },
        );
        if (stageIdx === 0 || stage.upscale === 1) {
            html = html.replace(/<select /, "<select disabled ");
        }
        return html;
    })();
    const samplerField = stageDropdownField(
        "sampler",
        "Sampler",
        defaults.samplerValues,
        defaults.samplerLabels,
        stage.sampler,
    );
    const schedulerField = stageDropdownField(
        "scheduler",
        "Scheduler",
        defaults.schedulerValues,
        defaults.schedulerLabels,
        stage.scheduler,
    );
    const vaeField = stageDropdownField(
        "vae",
        "VAE",
        defaults.vaeValues,
        defaults.vaeLabels,
        stage.vae,
    );
    const refStrengthFields = clip.refs
        .map((_ref, refIdx) =>
            stageSliderField(
                stageRefStrengthField(refIdx),
                `Reference Image ${refIdx} Strength`,
                stage.refStrengths[refIdx] ?? STAGE_REF_STRENGTH_DEFAULT,
                STAGE_REF_STRENGTH_MIN,
                STAGE_REF_STRENGTH_MAX,
                STAGE_REF_STRENGTH_STEP,
            ),
        )
        .join("");

    return `<section class="${cardClasses.join(" ")}" data-stage-idx="${stageIdx}">
            ${head}
            <div class="vs-card-body input-group-content">
                ${modelField}
                ${controlField}
                ${stepsField}
                ${cfgScaleField}
                ${upscaleField}
                ${upscaleMethodField}
                ${samplerField}
                ${schedulerField}
                ${vaeField}
                ${refStrengthFields}
            </div>
        </section>`;
};

export const renderClipCard = (
    clip: Clip,
    clipIdx: number,
    totalClips: number,
    getRootDefaults: () => RootDefaults,
): string => {
    const stagesCount = clip.stages.length;
    const refsCount = clip.refs.length;
    const skipBtnTitle = clip.skipped ? "Re-enable clip" : "Skip clip";
    const skipBtnVariant = clip.skipped ? "vs-btn-skip-active" : "";
    const collapseGlyph = clip.expanded ? "&#x2B9F;" : "&#x2B9E;";

    const groupClasses = ["input-group", "vs-clip-card"];
    groupClasses.push(
        clip.expanded ? "input-group-open" : "input-group-closed",
    );
    if (clip.skipped) {
        groupClasses.push("vs-skipped");
    }
    const contentStyle = clip.expanded ? "" : ' style="display: none;"';

    const head = `<span id="input_group_vsclip${clipIdx}" class="input-group-header input-group-shrinkable"><span class="header-label-wrap"><span class="auto-symbol">${collapseGlyph}</span><span class="header-label">${escapeAttr(clip.name)}</span><span class="header-label-spacer"></span><span class="vs-clip-card-actions"><button type="button" class="basic-button vs-btn-tiny ${skipBtnVariant}" data-clip-action="skip" data-clip-idx="${clipIdx}" title="${skipBtnTitle}">&#x23ED;&#xFE0E;</button><button type="button" class="interrupt-button vs-btn-tiny" data-clip-action="delete" data-clip-idx="${clipIdx}" title="Remove clip" ${totalClips === 1 ? "disabled" : ""}>&times;</button></span></span></span>`;

    const lengthField = injectFieldData(
        overrideSliderSteps(
            makeSliderInput(
                "",
                clipFieldId(clipIdx, "duration"),
                "duration",
                "Length (seconds)",
                "",
                clip.duration.toFixed(1),
                CLIP_DURATION_MIN,
                CLIP_DURATION_MAX,
                CLIP_DURATION_MIN,
                CLIP_DURATION_SLIDER_MAX,
                CLIP_DURATION_SLIDER_STEP,
                false,
                false,
                false,
            ),
            {
                numberStep: "any",
                rangeStep: CLIP_DURATION_SLIDER_STEP,
            },
        ),
        { "data-clip-field": "duration", "data-clip-idx": String(clipIdx) },
    );
    const audioSourceOptions = buildAudioSourceOptions();
    const audioSource = resolveAudioSourceValue(
        clip.audioSource,
        audioSourceOptions,
    );
    const audioSourceField = injectFieldData(
        buildNativeDropdown(
            clipFieldId(clipIdx, "audioSource"),
            "audioSource",
            "Audio Source",
            audioSourceOptions,
            audioSource,
        ),
        {
            "data-clip-field": "audioSource",
            "data-clip-idx": String(clipIdx),
        },
    );
    const audioUploadField = renderClipAudioUploadField(
        clip,
        clipIdx,
        audioSource,
    );

    const body = `
            <div class="input-group-content vs-clip-card-body" id="input_group_content_vsclip${clipIdx}" data-do_not_save="1"${contentStyle}>
                ${lengthField}
                ${audioSourceField}
                ${audioUploadField}

                <div class="vs-section-block">
                    <div class="vs-section-block-head">
                        <div class="vs-section-block-title">Reference Images &middot; ${refsCount}</div>
                    </div>
                <div class="vs-card-list">${clip.refs.map((ref, refIdx) => renderRefRow(ref, clip, clipIdx, refIdx, getRootDefaults)).join("")}</div>
                    <button type="button" class="vs-add-btn" data-clip-action="add-ref" data-clip-idx="${clipIdx}">+ Add Reference Image</button>
                </div>

                <div class="vs-section-block">
                    <div class="vs-section-block-head">
                        <div class="vs-section-block-title">Stages &middot; ${stagesCount}</div>
                    </div>
                    <div class="vs-card-list">${clip.stages.map((stage, stageIdx) => renderStageRow(clip, stage, clipIdx, stageIdx, getRootDefaults)).join("")}</div>
                    <button type="button" class="vs-add-btn" data-clip-action="add-stage" data-clip-idx="${clipIdx}">+ Add Video Stage</button>
                </div>
            </div>
        `;

    return `<div class="${groupClasses.join(" ")}" id="auto-group-vsclip${clipIdx}" data-clip-idx="${clipIdx}">${head}${body}</div>`;
};
