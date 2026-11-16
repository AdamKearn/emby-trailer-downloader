define(['baseView', 'loading', 'emby-input', 'emby-button', 'emby-select', 'emby-checkbox'], function (BaseView, loading) {
    'use strict';

    var pluginId = '5119c78b-fe33-4f70-90b9-6cccf1b59b7a';

    var checkboxes = ['EnableOnItemAdded', 'EnableMovies', 'EnableSeries', 'EnableSearchFallback', 'ManageDeno'];
    var texts = ['TmdbApiKey', 'PreferredLanguage', 'ProxyUrl', 'CookiesFile', 'YtDlpPathOverride', 'FfmpegPathOverride', 'ExtraYtDlpArgs'];
    var selects = ['ProxyMode', 'YtDlpChannel'];
    var numbers = ['ProcessDelayMinutes', 'MaxAttempts', 'MaxResolution'];

    function loadConfig(view) {
        loading.show();
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            checkboxes.forEach(function (k) { view.querySelector('#' + k).checked = !!config[k]; });
            texts.concat(selects).forEach(function (k) { view.querySelector('#' + k).value = config[k] || ''; });
            numbers.forEach(function (k) { view.querySelector('#' + k).value = config[k] != null ? config[k] : ''; });
            loading.hide();
        });
    }

    function saveConfig(view) {
        loading.show();
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            checkboxes.forEach(function (k) { config[k] = view.querySelector('#' + k).checked; });
            texts.concat(selects).forEach(function (k) { config[k] = view.querySelector('#' + k).value.trim(); });
            numbers.forEach(function (k) {
                var v = parseInt(view.querySelector('#' + k).value, 10);
                if (!isNaN(v)) config[k] = v;
            });
            ApiClient.updatePluginConfiguration(pluginId, config).then(function (result) {
                Dashboard.processPluginConfigurationUpdateResult(result);
            });
        });
    }

    function View(view, params) {
        BaseView.apply(this, arguments);
        view.querySelector('.trailerDownloaderConfigForm').addEventListener('submit', function (e) {
            e.preventDefault();
            saveConfig(view);
            return false;
        });
    }

    Object.assign(View.prototype, BaseView.prototype);

    View.prototype.onResume = function (options) {
        BaseView.prototype.onResume.apply(this, arguments);
        loadConfig(this.view);
    };

    return View;
});
