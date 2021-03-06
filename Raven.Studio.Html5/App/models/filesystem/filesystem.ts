﻿import resource = require("models/resource");
import license = require("models/license");

class filesystem extends resource {
    //isDefault = false;
    statistics = ko.observable<filesystemStatisticsDto>();    
    files = ko.observableArray<filesystemFileHeaderDto>();
    static type = 'filesystem';

    constructor(public name: string, isDisabled: boolean = false) {
        super(name, filesystem.type);
        this.disabled(isDisabled);
        this.itemCount = ko.computed(() => this.statistics() ? this.statistics().FileCount : 0);
        this.itemCountText = ko.computed(() => {
            var itemCount = this.itemCount();
            var text = itemCount + ' file';
            if (itemCount != 1) {
                text += 's';
            }
            return text;
        });
        this.isLicensed = ko.computed(() => {
            if (!!license.licenseStatus() && license.licenseStatus().IsCommercial) {
                var ravenFsValue = license.licenseStatus().Attributes.ravenfs;
                return /^true$/i.test(ravenFsValue);
            }
            return true;
        });
    }

    activate() {
        ko.postbox.publish("ActivateFilesystem", this);
    }

    static getNameFromUrl(url: string) {
        var index = url.indexOf("filesystems/");
        return (index > 0) ? url.substring(index + 10) : "";
    }
}
export = filesystem;