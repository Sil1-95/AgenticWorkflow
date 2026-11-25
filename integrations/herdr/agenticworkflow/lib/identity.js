// Tells a person when the cockpit and the `aw` it drives came from different installations. A cockpit copy, the `aw` program and the controller are one product with one version; a
// mismatch means a partial update or a stale copy, which is reported in words instead of failing in confusing ways later.

/**
 * @param {string|null|undefined} cockpitVersion  the version stamped into this cockpit copy (null in a source checkout)
 * @param {string|null|undefined} controllerVersion  the version the running controller (and so `aw`) reports
 * @returns {{text:string}|null}  a notice to show, or null when there is nothing to say
 */
export function identityNotice(cockpitVersion, controllerVersion) {
  if (!cockpitVersion || !controllerVersion) return null;
  if (cockpitVersion === controllerVersion) return null;
  return {
    text: `This cockpit is version ${cockpitVersion} but AgenticWorkflow is version ${controllerVersion}. They come from different installations. Run install.ps1 from the newer release package, then reopen the cockpit. (aw doctor shows the details.)`,
  };
}

/** The controller version in an `aw ctl health` result, or null when it is not there. */
export function controllerVersionOf(health) {
  const version = health?.controller?.version;
  return typeof version === "string" && version ? version : null;
}
