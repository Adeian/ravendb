import commandBase = require("commands/commandBase");
import database = require("models/database");
import collectionInfo = require("models/collectionInfo");
import collection = require("models/collection");
import pagedResultSet = require("common/pagedResultSet");

class getDocumentsCommand extends commandBase {

    constructor(private collection: collection, private db: database, private skip: number, private take: number) {
        super();
    }

    execute(): JQueryPromise<pagedResultSet> {
        var args = {
            query: "Tag:" + this.collection.isAllDocuments ? '' : this.collection.name,
            start: this.skip,
            pageSize: this.take
        };

        var resultsSelector = (dto: collectionInfoDto) => new collectionInfo(dto);
        var url =  "/indexes/Raven/DocumentsByEntityName";
        var documentsTask = $.Deferred();
        this.query(url, args, this.db, resultsSelector)
            .then(collection => {
                var items = collection.results;
                var resultSet = new pagedResultSet(items, collection.totalResults);
                documentsTask.resolve(resultSet);
            });
        return documentsTask;
    }
}

export = getDocumentsCommand;