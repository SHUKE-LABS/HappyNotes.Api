USE HappyNotes;

CREATE TABLE IF NOT EXISTS FanfouUserAccount
(
    Id                BIGINT AUTO_INCREMENT PRIMARY KEY,
    UserId            BIGINT       NOT NULL,
    FanfouUserId      VARCHAR(255) NOT NULL DEFAULT '',
    AccessToken       VARCHAR(512) NOT NULL,
    AccessTokenSecret VARCHAR(512) NOT NULL,
    Status            INT          NOT NULL COMMENT 'Reference FanfouUserAccountStatus enum for details',
    SyncType          INT          NOT NULL DEFAULT '1' COMMENT 'FanfouSyncType 1 All 2 PublicOnly 3 TagFanfouOnly',
    CreatedAt         BIGINT       NOT NULL,
    UNIQUE KEY (UserId)
);

-- Check if the 'FanfouStatusIds' field exists on Note
SELECT COUNT(*)
INTO @exists
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = 'HappyNotes'
  AND TABLE_NAME = 'Note'
  AND COLUMN_NAME = 'FanfouStatusIds';

-- If the 'FanfouStatusIds' field does not exist, add it
SET @sql = IF(@exists = 0,
              'ALTER TABLE `HappyNotes`.`Note` ADD `FanfouStatusIds` VARCHAR(512) NULL COMMENT ''Comma-separated UserAccountId:StatusId list'' AFTER `MastodonTootIds`;',
              'SELECT ''Column FanfouStatusIds already exists.'' AS Result');

PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;
